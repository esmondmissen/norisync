using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Narrows an HLS master playlist to the single best rendition before ffmpeg is pointed at it.
/// Apple's motion-cover masters list dozens of renditions, and ffmpeg opens every one of them while
/// probing the input — that burst of parallel connections is what makes the CDN cut responses off
/// mid-segment ("Stream ends prematurely"), leaving corrupt packets in the encode. Resolving the
/// master here means ffmpeg sees one media playlist and opens one connection.
/// </summary>
public sealed class HlsVariantResolver : IDisposable
{
    // A master playlist is a few kilobytes of text. Anything beyond this is not one, and reading it
    // would only be a way to be fed an unbounded body.
    private const int MaxPlaylistBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger<HlsVariantResolver> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsVariantResolver"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public HlsVariantResolver(ILogger<HlsVariantResolver> logger)
    {
        _logger = logger;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxPlaylistBytes
        };
    }

    /// <summary>
    /// Returns the URL ffmpeg should be given for a motion-cover source. For an HLS master playlist
    /// that is one rendition's media playlist, chosen against <paramref name="targetShortEdge"/>; for
    /// anything else — a direct MP4, a media playlist, an unreachable or unparseable master — it is
    /// <paramref name="sourceUrl"/> unchanged, so a failure here can only cost the old behaviour,
    /// never the fetch.
    /// </summary>
    /// <param name="sourceUrl">The motion-cover URL from the artwork service.</param>
    /// <param name="targetShortEdge">The short edge the fetch will scale to; zero or negative
    /// means no cap, in which case the largest rendition is taken.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The chosen rendition, or <see langword="null"/> to use <paramref name="sourceUrl"/> as-is.</returns>
    public async Task<HlsRendition?> ResolveBestAsync(string sourceUrl, int targetShortEdge, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!sourceUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        string playlist;
        try
        {
            playlist = await _http.GetStringAsync(baseUri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Timed out reading HLS master playlist {Url}; using it as-is", sourceUrl);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Could not read HLS master playlist {Url}; using it as-is", sourceUrl);
            return null;
        }

        var best = SelectBestVariant(playlist, baseUri, targetShortEdge);
        if (best == null)
        {
            return null;
        }

        _logger.LogDebug(
            "Resolved HLS master {Url} to {ShortEdge}px {Codecs} rendition {Variant}",
            sourceUrl,
            best.ShortEdge,
            best.Codecs,
            best.Url);
        return best;
    }

    /// <summary>
    /// Picks the rendition to download out of a master playlist: the <em>smallest</em> one whose short
    /// edge still reaches <paramref name="targetShortEdge"/>, since the fetch scales to that edge
    /// anyway and a larger source only costs bandwidth for pixels that get thrown away. Falls back to
    /// the largest rendition when none reaches the target or no target is given, and to the highest
    /// declared bandwidth when renditions carry no <c>RESOLUTION</c>. Returns <see langword="null"/>
    /// when the text is not a master playlist (no <c>EXT-X-STREAM-INF</c> entries), which is the
    /// signal to use the original URL.
    /// </summary>
    /// <param name="playlist">The master playlist text.</param>
    /// <param name="baseUri">The playlist's own URL, used to resolve relative rendition URIs.</param>
    /// <param name="targetShortEdge">The short edge the fetch will scale to; zero or negative
    /// means no cap.</param>
    /// <returns>The chosen rendition, or <see langword="null"/>.</returns>
    internal static HlsRendition? SelectBestVariant(string playlist, Uri baseUri, int targetShortEdge)
    {
        var renditions = ParseRenditions(playlist, baseUri);
        if (renditions.Count == 0)
        {
            return null;
        }

        HlsRendition? best = null;
        if (targetShortEdge > 0)
        {
            // Smallest that still covers the target; ties broken by the better-quality source.
            foreach (var candidate in renditions)
            {
                if (candidate.ShortEdge < targetShortEdge)
                {
                    continue;
                }

                if (best == null
                    || candidate.ShortEdge < best.ShortEdge
                    || (candidate.ShortEdge == best.ShortEdge && candidate.Bandwidth > best.Bandwidth))
                {
                    best = candidate;
                }
            }
        }

        // Nothing reaches the target (or no target): take the largest available instead of
        // upscaling from something small.
        if (best == null)
        {
            foreach (var candidate in renditions)
            {
                if (best == null
                    || candidate.ShortEdge > best.ShortEdge
                    || (candidate.ShortEdge == best.ShortEdge && candidate.Bandwidth > best.Bandwidth))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _http.Dispose();
        _disposed = true;
    }

    // Walks the playlist pairing each EXT-X-STREAM-INF with the rendition URI that follows it.
    private static List<HlsRendition> ParseRenditions(string playlist, Uri baseUri)
    {
        var renditions = new List<HlsRendition>();
        var pendingShortEdge = -1;
        var pendingBandwidth = -1L;
        string? pendingCodecs = null;
        var pending = false;

        foreach (var rawLine in playlist.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                pending = true;
                pendingShortEdge = ReadResolutionShortEdge(line);
                pendingBandwidth = ReadAttributeValue(line, "BANDWIDTH");
                pendingCodecs = ReadAttributeText(line, "CODECS");
                continue;
            }

            // The first non-comment line after an EXT-X-STREAM-INF is that rendition's URI; any
            // other tag in between is skipped.
            if (line[0] == '#' || !pending)
            {
                continue;
            }

            pending = false;
            if (Uri.TryCreate(baseUri, line, out var absolute))
            {
                renditions.Add(new HlsRendition
                {
                    Url = absolute.ToString(),
                    ShortEdge = pendingShortEdge,
                    Bandwidth = pendingBandwidth,
                    Codecs = pendingCodecs
                });
            }
        }

        return renditions;
    }

    // RESOLUTION=1080x1440 -> 1080. Negative when absent or malformed.
    private static int ReadResolutionShortEdge(string line)
    {
        var value = ReadAttributeText(line, "RESOLUTION");
        if (value == null)
        {
            return -1;
        }

        var x = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (x <= 0
            || !int.TryParse(value[..x], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(value[(x + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
        {
            return -1;
        }

        return Math.Min(width, height);
    }

    private static long ReadAttributeValue(string line, string name)
        => long.TryParse(ReadAttributeText(line, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : -1;

    // Reads one attribute out of an EXT-X-STREAM-INF line. The name must sit at an attribute
    // boundary so AVERAGE-BANDWIDTH is not mistaken for BANDWIDTH.
    private static string? ReadAttributeText(string line, string name)
    {
        var search = name + "=";
        var index = line.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        while (index > 0)
        {
            var previous = line[index - 1];
            if (previous == ',' || previous == ':')
            {
                var start = index + search.Length;

                // A quoted value may itself contain commas (CODECS="avc1.640028,mp4a.40.2"), so it
                // ends at the closing quote rather than the next separator.
                if (start < line.Length && line[start] == '"')
                {
                    var closing = line.IndexOf('"', start + 1);
                    return closing < 0 ? line[(start + 1)..] : line[(start + 1)..closing];
                }

                var end = line.IndexOf(',', start);
                return (end < 0 ? line[start..] : line[start..end]).Trim();
            }

            index = line.IndexOf(search, index + search.Length, StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }
}

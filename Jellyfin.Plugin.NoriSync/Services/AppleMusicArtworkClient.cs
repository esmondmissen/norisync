using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Thin client for the <c>apple-music-animated-artworks</c> service
/// (<see href="https://github.com/m8tec/apple-music-animated-artworks"/>), which the operator hosts
/// themselves — no host ships with the plugin. Resolves an artist/album pair to the square and/or
/// tall motion-cover source URLs by calling <c>GET /api/v1/artwork/search</c>.
/// Outbound calls are serialized through a semaphore with a minimum gap so a self-hosted scraper —
/// which in turn hits Apple — is not hammered during a full-library backfill.
/// </summary>
public sealed class AppleMusicArtworkClient : IDisposable
{
    private static readonly TimeSpan _minimumGap = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly ILogger<AppleMusicArtworkClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppleMusicArtworkClient"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public AppleMusicArtworkClient(ILogger<AppleMusicArtworkClient> logger)
    {
        _logger = logger;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        // A browser-like User-Agent gives the best chance of clearing the public host's bot filter;
        // a self-hosted instance ignores it.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; Jellyfin-NoriSync/1.0; +https://github.com/jellyfin-plugin-norisync)");
    }

    /// <summary>
    /// Resolves the motion-cover source URLs for an album by querying the service's search endpoint.
    /// Returns <see langword="null"/> when the base URL is invalid, the service is unreachable, or no
    /// match is found; a non-null result may still have only one of the two variants populated.
    /// </summary>
    /// <param name="baseUrl">The service base URL (from configuration).</param>
    /// <param name="artist">The album artist name.</param>
    /// <param name="album">The album title.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved match, or <see langword="null"/>.</returns>
    public async Task<AppleArtworkMatch?> ResolveAsync(string baseUrl, string artist, string album, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.TrimEnd('/') + "/api/v1/artwork/search", UriKind.Absolute, out var endpoint))
        {
            _logger.LogWarning("Apple artwork service URL is not a valid absolute URL: {Url}", baseUrl);
            return null;
        }

        var requestUri = string.Create(
            CultureInfo.InvariantCulture,
            $"{endpoint}?artist={Uri.EscapeDataString(artist)}&album={Uri.EscapeDataString(album)}");

        string body;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastCall;
            if (elapsed < _minimumGap)
            {
                await Task.Delay(_minimumGap - elapsed, cancellationToken).ConfigureAwait(false);
            }

            using var response = await _http.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            _lastCall = DateTimeOffset.UtcNow;

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // The service returns 404 for "no artwork found" — a normal miss, not an error.
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Apple artwork lookup for {Artist} - {Album} returned {Status}",
                    artist,
                    album,
                    (int)response.StatusCode);
                return null;
            }

            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var match = ParseMatch(doc.RootElement);
            if (match == null || !match.HasAny)
            {
                // Body parsed but yielded no usable variant URL — most likely the response shape
                // differs from what this parser expects. Log a truncated sample so the field names
                // can be reconciled against a self-hosted instance.
                _logger.LogDebug(
                    "Apple artwork response for {Artist} - {Album} had no usable variant URL. Body sample: {Body}",
                    artist,
                    album,
                    Truncate(body, 500));
                return null;
            }

            return match;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Apple artwork response for {Artist} - {Album}", artist, album);
            return null;
        }
    }

    /// <summary>
    /// Extracts the square and tall motion-cover URLs from a search response. Tolerant of shape: it
    /// scans the whole document for video URLs (<c>.m3u8</c>/<c>.mp4</c>) and classifies each by the
    /// property names and any adjacent orientation/type descriptor around it. Direct MP4s are
    /// preferred over HLS for the same variant.
    /// </summary>
    /// <param name="root">The parsed response root.</param>
    /// <returns>The extracted match, or <see langword="null"/> when nothing usable was found.</returns>
    internal static AppleArtworkMatch? ParseMatch(JsonElement root)
    {
        var found = new List<(string Hint, string Url)>();
        Collect(root, string.Empty, found, 0);

        string? square = null;
        var squareIsMp4 = false;
        string? tall = null;
        var tallIsMp4 = false;
        string? fallback = null;
        var fallbackIsMp4 = false;

        foreach (var (hint, url) in found)
        {
            var isMp4 = url.Contains(".mp4", StringComparison.OrdinalIgnoreCase);
            var isTall = hint.Contains("tall", StringComparison.OrdinalIgnoreCase)
                || hint.Contains("vertical", StringComparison.OrdinalIgnoreCase)
                || hint.Contains("portrait", StringComparison.OrdinalIgnoreCase);
            var isSquare = hint.Contains("square", StringComparison.OrdinalIgnoreCase)
                || hint.Contains("cover", StringComparison.OrdinalIgnoreCase)
                || hint.Contains("primary", StringComparison.OrdinalIgnoreCase);

            if (isTall)
            {
                Prefer(ref tall, ref tallIsMp4, url, isMp4);
            }
            else if (isSquare)
            {
                Prefer(ref square, ref squareIsMp4, url, isMp4);
            }
            else
            {
                Prefer(ref fallback, ref fallbackIsMp4, url, isMp4);
            }
        }

        // An unclassified URL (no orientation hint) is the standard square cover by convention.
        square ??= fallback;

        if (square == null && tall == null)
        {
            return null;
        }

        return new AppleArtworkMatch { SquareUrl = square, TallUrl = tall };
    }

    private static void Prefer(ref string? current, ref bool currentIsMp4, string candidate, bool candidateIsMp4)
    {
        if (current == null || (candidateIsMp4 && !currentIsMp4))
        {
            current = candidate;
            currentIsMp4 = candidateIsMp4;
        }
    }

    // Recursively walks the JSON, collecting (hint, url) pairs for every string value that looks like
    // a video URL. The hint accumulates the property-name path plus any orientation/type descriptor
    // sitting alongside the URL in the same object, so the classifier can tell square from tall.
    private static void Collect(JsonElement element, string hint, List<(string Hint, string Url)> found, int depth)
    {
        if (depth > 12)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var localDescriptor = ReadDescriptor(element);
                foreach (var prop in element.EnumerateObject())
                {
                    var childHint = string.Concat(hint, " ", prop.Name, " ", localDescriptor);
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = prop.Value.GetString();
                        if (IsVideoUrl(value))
                        {
                            found.Add((childHint, value!));
                        }
                    }
                    else
                    {
                        Collect(prop.Value, childHint, found, depth + 1);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, hint, found, depth + 1);
                }

                break;
        }
    }

    // Pulls an orientation/type descriptor out of an object's own scalar fields (e.g. a
    // { "type": "tall", "url": "..." } entry), so a URL keyed only as "url" still gets classified.
    private static string ReadDescriptor(JsonElement obj)
    {
        string[] keys = { "type", "kind", "orientation", "variant", "name", "label", "aspect", "aspectRatio" };
        var descriptor = string.Empty;
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                descriptor = string.Concat(descriptor, " ", value.GetString());
            }
        }

        return descriptor;
    }

    private static bool IsVideoUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".mp4", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _http.Dispose();
        _gate.Dispose();
        _disposed = true;
    }
}

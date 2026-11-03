using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Rewrites animated artwork with the ffmpeg binary Jellyfin ships. Assets are transcoded once and
/// then served as static files, so range requests and the SHA-256 ETag keep working. Renditions are
/// cut to a short-edge cap as faststart H.264 MP4; MP4s that merely lack faststart are remuxed with
/// the streams copied. Sources with an alpha channel are never re-encoded, because H.264 yuv420p
/// would flatten the transparency.
/// </summary>
public sealed class ArtworkTranscoder
{
    // Substitute short-edge cap when no downscale is wanted but a re-encode still runs (bitrate
    // overage): scale then only enforces even dimensions, which yuv420p H.264 requires.
    private const int NoDimensionCap = 16384;

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<ArtworkTranscoder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkTranscoder"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Jellyfin media encoder (provides the ffmpeg binary path).</param>
    /// <param name="logger">Logger instance.</param>
    public ArtworkTranscoder(IMediaEncoder mediaEncoder, ILogger<ArtworkTranscoder> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Detects whether a pixel format carries an alpha channel (e.g. <c>yuva420p</c>, <c>rgba</c>),
    /// which a re-encode to H.264 <c>yuv420p</c> would flatten.
    /// </summary>
    /// <param name="pixelFormat">The ffprobe <c>pix_fmt</c> value.</param>
    /// <returns>True when the format has alpha; false when it does not or is unknown.</returns>
    public static bool HasAlpha(string? pixelFormat)
    {
        if (string.IsNullOrEmpty(pixelFormat))
        {
            return false;
        }

        return pixelFormat.StartsWith("yuva", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.StartsWith("rgba", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.StartsWith("bgra", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.StartsWith("argb", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.StartsWith("abgr", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.StartsWith("gbrap", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.StartsWith("ya", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-encodes a source video to faststart H.264 MP4, scaled so its short edge meets the cap
    /// (aspect ratio preserved, never upscaled) and rate-limited to the bitrate cap. Audio is
    /// dropped — the asset is artwork. The source file is left in place.
    /// </summary>
    /// <param name="sourcePath">The source video file.</param>
    /// <param name="destinationPath">Where to write the MP4 output.</param>
    /// <param name="shortEdgeCap">Short-edge pixel cap; zero or negative means no downscale.</param>
    /// <param name="maxBitrateKbps">Bitrate ceiling in kbps; zero or negative means CRF only.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when ffmpeg produced the output; false on failure (logged, not thrown).</returns>
    public Task<bool> TranscodeAsync(string sourcePath, string destinationPath, int shortEdgeCap, int maxBitrateKbps, CancellationToken cancellationToken)
        => RunFfmpegAsync(BuildTranscodeArguments(sourcePath, destinationPath, shortEdgeCap, maxBitrateKbps), sourcePath, cancellationToken);

    /// <summary>
    /// Remuxes an MP4-family upload with the streams copied, moving <c>moov</c> to the front
    /// (faststart) so players can start before the whole file lands. The source file is left in
    /// place.
    /// </summary>
    /// <param name="sourcePath">The staged upload.</param>
    /// <param name="destinationPath">Where to write the MP4 output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when ffmpeg produced the output; false on failure (logged, not thrown).</returns>
    public Task<bool> RemuxFaststartAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        => RunFfmpegAsync(BuildRemuxArguments(sourcePath, destinationPath), sourcePath, cancellationToken);

    /// <summary>
    /// Fetches a remote motion cover — a direct MP4 or an HLS <c>.m3u8</c> playlist — and writes it
    /// as faststart H.264 MP4, scaled so its short edge meets the cap (aspect ratio preserved, never
    /// upscaled) and rate-limited to the bitrate cap. Audio is dropped. Used by the Apple animated
    /// artwork backfill, where the source is always opaque, so a single re-encode path covers both
    /// container types and guarantees the stored asset is faststart at the target resolution.
    /// </summary>
    /// <param name="sourceUrl">The remote motion-cover URL (MP4 or HLS playlist).</param>
    /// <param name="destinationPath">Where to write the MP4 output.</param>
    /// <param name="shortEdgeCap">Short-edge pixel cap; zero or negative means no downscale.</param>
    /// <param name="maxBitrateKbps">Bitrate ceiling in kbps; zero or negative means CRF only.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when ffmpeg produced the output; false on failure (logged, not thrown).</returns>
    public Task<bool> FetchToMp4Async(string sourceUrl, string destinationPath, int shortEdgeCap, int maxBitrateKbps, CancellationToken cancellationToken)
        => RunFfmpegAsync(BuildFetchArguments(sourceUrl, destinationPath, shortEdgeCap, maxBitrateKbps), sourceUrl, cancellationToken);

    /// <summary>
    /// Fetches a remote motion cover without re-encoding it: the video stream is copied into a
    /// faststart MP4, so the cost is the download plus a container rewrite. Used when the source
    /// rendition is already the size we want, which is the common case once the HLS master has been
    /// narrowed to the rendition nearest the target — a re-encode there would spend CPU to make the
    /// picture slightly worse. Audio is dropped; the source file is left in place.
    /// </summary>
    /// <param name="sourceUrl">The remote motion-cover URL (MP4 or HLS playlist).</param>
    /// <param name="destinationPath">Where to write the MP4 output.</param>
    /// <param name="isHevc">Whether the source video is HEVC, which needs the <c>hvc1</c> sample-entry
    /// tag written for Apple players to accept the copied stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when ffmpeg produced the output; false on failure (logged, not thrown).</returns>
    public Task<bool> FetchCopyToMp4Async(string sourceUrl, string destinationPath, bool isHevc, CancellationToken cancellationToken)
        => RunFfmpegAsync(BuildFetchCopyArguments(sourceUrl, destinationPath, isHevc), sourceUrl, cancellationToken);

    internal static string BuildFetchCopyArguments(string sourceUrl, string destinationPath, bool isHevc)
    {
        // ffmpeg defaults the HEVC sample entry to hev1 when muxing into MP4; AVPlayer and QuickTime
        // only accept hvc1. Copying H.264 needs no such coaxing.
        var tag = isHevc ? "-tag:v hvc1 " : string.Empty;

        return "-nostdin -hide_banner -loglevel warning -y "
            + "-protocol_whitelist file,http,https,tcp,tls,crypto "
            + InputOptionsFor(sourceUrl)
            + $"-i \"{sourceUrl}\" -an -sn -dn "
            + $"-c:v copy {tag}-movflags +faststart -f mp4 \"{destinationPath}\"";
    }

    internal static string BuildFetchArguments(string sourceUrl, string destinationPath, int shortEdgeCap, int maxBitrateKbps)
    {
        var filter = BuildScaleFilter(shortEdgeCap);
        var rate = maxBitrateKbps > 0
            ? string.Create(CultureInfo.InvariantCulture, $"-maxrate {maxBitrateKbps}k -bufsize {maxBitrateKbps * 2}k ")
            : string.Empty;

        // protocol_whitelist lets the HLS demuxer follow the playlist's https segment URLs; it is a
        // format-context option, so it is harmless for a direct MP4 input.
        //
        // Deliberately no -map: an HLS media playlist can still expose more than one stream, and
        // "0:v:0" pins the first-listed one rather than the best. ffmpeg's default stream selection
        // picks the highest-resolution video stream; -an/-sn/-dn drop everything that is not video.
        //
        // loglevel warning (not error) so segment fetch problems that ffmpeg recovers from — the
        // ones that silently shorten or corrupt the output — reach the log.
        return "-nostdin -hide_banner -loglevel warning -y "
            + "-protocol_whitelist file,http,https,tcp,tls,crypto "
            + InputOptionsFor(sourceUrl)
            + $"-i \"{sourceUrl}\" -an -sn -dn "
            + $"-vf {filter} -c:v libx264 -preset veryfast -crf 23 {rate}"
            + $"-pix_fmt yuv420p -movflags +faststart -f mp4 \"{destinationPath}\"";
    }

    internal static string BuildTranscodeArguments(string sourcePath, string destinationPath, int shortEdgeCap, int maxBitrateKbps)
    {
        var filter = BuildScaleFilter(shortEdgeCap);
        var rate = maxBitrateKbps > 0
            ? string.Create(CultureInfo.InvariantCulture, $"-maxrate {maxBitrateKbps}k -bufsize {maxBitrateKbps * 2}k ")
            : string.Empty;

        return $"-nostdin -hide_banner -loglevel error -y -i \"{sourcePath}\" -map 0:v:0 -an -sn -dn "
            + $"-vf {filter} -c:v libx264 -preset veryfast -crf 23 {rate}"
            + $"-pix_fmt yuv420p -movflags +faststart -f mp4 \"{destinationPath}\"";
    }

    /// <summary>
    /// Builds the scale filter: shrink until the short edge meets the cap, never above the source,
    /// then floor both sides to even (yuv420p H.264 requires it). A quality step names the short
    /// edge, so a 16:9 source at 1080 is 1920x1080 and a 3:4 source is 1080x1440 — the "1080p"
    /// reading in either orientation.
    /// </summary>
    /// <param name="shortEdgeCap">Short-edge pixel cap; zero or negative means no downscale.</param>
    /// <returns>The <c>-vf</c> value, with no whitespace so it stays a single argv token.</returns>
    private static string BuildScaleFilter(int shortEdgeCap)
    {
        var cap = shortEdgeCap > 0 ? shortEdgeCap : NoDimensionCap;

        // Written out rather than using force_original_aspect_ratio=increase, whose companion
        // force_divisible_by rounds *up* in that mode — an odd-dimensioned source would gain a pixel
        // instead of losing one, which for the NoDimensionCap path means upscaling a file we were
        // asked not to resize. trunc(x/2)*2 only ever floors.
        //
        // Keep the multiply before the divide: iw*cap/min(iw,ih) is evaluated left to right and is
        // exact, whereas iw*(cap/min(iw,ih)) forms an inexact reciprocal first and can land on 719.99
        // where 720 is meant, costing 2px after the floor.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"scale=w='trunc(min(iw*{cap}/min(iw,ih),iw)/2)*2':h='trunc(min(ih*{cap}/min(iw,ih),ih)/2)*2'");
    }

    /// <summary>
    /// The dimensions <see cref="BuildScaleFilter"/> will produce, computed in C# so the contract is
    /// testable without running ffmpeg. Integer division is equivalent to ffmpeg's floating-point
    /// <c>trunc</c>: flooring is monotone and the outer even-floor subsumes the inner one.
    /// </summary>
    /// <param name="shortEdgeCap">Short-edge pixel cap; zero or negative means no downscale.</param>
    /// <param name="width">The source width in pixels.</param>
    /// <param name="height">The source height in pixels.</param>
    /// <returns>The output width and height.</returns>
    internal static (int Width, int Height) PredictScaledSize(int shortEdgeCap, int width, int height)
    {
        var cap = shortEdgeCap > 0 ? shortEdgeCap : NoDimensionCap;
        var shortSide = Math.Min(width, height);
        if (shortSide <= 0)
        {
            return (width, height);
        }

        var w = (int)Math.Min((long)width * cap / shortSide, width) / 2 * 2;
        var h = (int)Math.Min((long)height * cap / shortSide, height) / 2 * 2;
        return (w, h);
    }

    // Input options gated on the source kind: ffmpeg treats an input option that no demuxer or
    // protocol consumes as fatal ("Option ... not found"), so the HLS-only ones must not be passed
    // for a direct MP4, nor the http ones for a local file.
    //
    // reconnect* are http-protocol options: they let a dropped connection resume instead of silently
    // costing the rest of the file.
    //
    // http_persistent=0 disables connection reuse between segment requests. Apple's CDN closes
    // kept-alive connections part-way through a response, which ffmpeg reports as "keepalive request
    // failed" / "Stream ends prematurely" and turns into corrupt packets in the output.
    //
    // live_start_index=0 only matters for a playlist ffmpeg reads as live (no #EXT-X-ENDLIST), where
    // it would otherwise start three segments from the end; Apple's are VOD, but other sources may
    // not be.
    private static string InputOptionsFor(string sourceUrl)
    {
        var options = string.Empty;
        if (sourceUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            options += "-live_start_index 0 -http_persistent 0 ";
        }

        if (sourceUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            options += "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 ";
        }

        return options;
    }

    internal static string BuildRemuxArguments(string sourcePath, string destinationPath)
        => $"-nostdin -hide_banner -loglevel error -y -i \"{sourcePath}\" -map 0 -c copy -movflags +faststart -f mp4 \"{destinationPath}\"";

    private async Task<bool> RunFfmpegAsync(string args, string sourcePath, CancellationToken cancellationToken)
    {
        var ffmpeg = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogWarning("ffmpeg binary path is not available from IMediaEncoder");
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            _logger.LogWarning("Failed to launch ffmpeg for artwork {Path}", sourcePath);
            return false;
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            _logger.LogWarning("ffmpeg exited with {Code} rewriting artwork {Path}: {Stderr}", process.ExitCode, sourcePath, stderr);
            return false;
        }

        // A zero exit with diagnostics on stderr is the shape of a partially-read HLS source:
        // ffmpeg gives up on a segment, writes what it has, and reports success. Surface it.
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            _logger.LogWarning("ffmpeg succeeded with diagnostics for artwork {Path}: {Stderr}", sourcePath, stderr);
        }

        return true;
    }
}

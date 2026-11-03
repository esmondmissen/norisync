using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Validates and probes an uploaded animated-artwork file (a short looping video) with ffprobe.
/// Confirms it is an MP4-family container carrying a video stream, and extracts the codec,
/// dimensions, duration, and <c>moov</c> position the store and serve endpoints report back.
/// </summary>
public sealed class ArtworkProbe
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<ArtworkProbe> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkProbe"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Jellyfin media encoder (provides the ffprobe binary path).</param>
    /// <param name="logger">Logger instance.</param>
    public ArtworkProbe(IMediaEncoder mediaEncoder, ILogger<ArtworkProbe> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Probes a staged artwork file. Returns <see langword="null"/> when ffprobe cannot read the file,
    /// the container is not MP4-family, or it carries no video stream.
    /// </summary>
    /// <param name="path">Absolute path of the staged file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The probe result, or <see langword="null"/> when the file is not valid animated artwork.</returns>
    public async Task<ArtworkProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var json = await RunFfprobeAsync(path, cancellationToken).ConfigureAwait(false);
        if (json == null)
        {
            return null;
        }

        try
        {
            return Parse(json, path);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse ffprobe JSON for artwork {Path}", path);
            return null;
        }
    }

    private ArtworkProbeResult? Parse(string json, string path)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string formatName = string.Empty;
        double durationSeconds = 0;
        long reportedBitRate = 0;
        if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object)
        {
            formatName = GetString(format, "format_name") ?? string.Empty;
            durationSeconds = ParseDouble(GetString(format, "duration"));
            reportedBitRate = ParseLong(GetString(format, "bit_rate"));
        }

        var isMp4Family = formatName.Contains("mp4", StringComparison.OrdinalIgnoreCase)
            || formatName.Contains("mov", StringComparison.OrdinalIgnoreCase)
            || formatName.Contains("m4v", StringComparison.OrdinalIgnoreCase)
            || formatName.Contains("isom", StringComparison.OrdinalIgnoreCase)
            || formatName.Contains("webm", StringComparison.OrdinalIgnoreCase)
            || formatName.Contains("matroska", StringComparison.OrdinalIgnoreCase);
        if (!isMp4Family)
        {
            return null;
        }

        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? videoCodec = null;
        string? pixelFormat = null;
        var alphaModeTag = false;
        int width = 0;
        int height = 0;
        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.TryGetProperty("codec_type", out var type)
                && string.Equals(type.GetString(), "video", StringComparison.Ordinal))
            {
                videoCodec = GetString(stream, "codec_name");
                pixelFormat = GetString(stream, "pix_fmt");

                // WebM stores VP9 alpha as a separate plane flagged only by this container-level
                // tag; the stream's pix_fmt still reads as an opaque format.
                if (stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                {
                    alphaModeTag = string.Equals(GetString(tags, "alpha_mode"), "1", StringComparison.Ordinal);
                }

                width = stream.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
                height = stream.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : 0;

                if (durationSeconds <= 0)
                {
                    durationSeconds = ParseDouble(GetString(stream, "duration"));
                }

                if (reportedBitRate <= 0)
                {
                    reportedBitRate = ParseLong(GetString(stream, "bit_rate"));
                }

                break;
            }
        }

        if (videoCodec == null)
        {
            _logger.LogWarning("Artwork file {Path} has no video stream", path);
            return null;
        }

        // ffprobe's format.duration is only a bitrate estimate for a fragmented MP4 whose moov
        // header carries no duration (common for machine-generated artwork clips). Read the real
        // duration straight from the box structure for MP4/MOV containers; ffprobe stays the source
        // for WebM/Matroska, whose headers do carry an accurate duration.
        var isMp4Container = formatName.Contains("mp4", StringComparison.OrdinalIgnoreCase)
            || formatName.Contains("mov", StringComparison.OrdinalIgnoreCase)
            || formatName.Contains("m4v", StringComparison.OrdinalIgnoreCase)
            || formatName.Contains("isom", StringComparison.OrdinalIgnoreCase);
        if (isMp4Container && Mp4DurationReader.TryReadDurationSeconds(path) is double boxDuration && boxDuration > 0)
        {
            durationSeconds = boxDuration;
        }

        // The overall bitrate is what a bandwidth cap meters: bytes served per second of playback.
        // Derive it from the file size and the (corrected) duration when possible — ffprobe's
        // reported bit_rate is itself computed from its own duration estimate, which the fragmented
        // MP4 correction above just fixed. The reported value stays as the no-duration fallback.
        long bitRate = 0;
        if (durationSeconds > 0)
        {
            bitRate = (long)(new FileInfo(path).Length * 8 / durationSeconds);
        }

        if (bitRate <= 0)
        {
            bitRate = reportedBitRate;
        }

        return new ArtworkProbeResult
        {
            FormatName = formatName,
            VideoCodec = videoCodec,
            Width = width > 0 ? width : null,
            Height = height > 0 ? height : null,
            DurationSeconds = durationSeconds > 0 ? durationSeconds : null,
            BitRateBps = bitRate > 0 ? bitRate : null,
            PixelFormat = pixelFormat,
            HasAlpha = alphaModeTag || ArtworkTranscoder.HasAlpha(pixelFormat),
            MoovAtFront = AudioInfoProbe.DetectMoovAtFront(path)
        };
    }

    private async Task<string?> RunFfprobeAsync(string path, CancellationToken cancellationToken)
    {
        var ffprobe = _mediaEncoder.ProbePath;
        if (string.IsNullOrEmpty(ffprobe))
        {
            _logger.LogWarning("ffprobe binary path is not available from IMediaEncoder");
            return null;
        }

        var args = string.Create(
            CultureInfo.InvariantCulture,
            $"-v error -print_format json -show_streams -show_format \"{path}\"");

        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            _logger.LogWarning("Failed to launch ffprobe for artwork {Path}", path);
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            _logger.LogWarning("ffprobe exited with {Code} for artwork {Path}: {Stderr}", process.ExitCode, path, stderr);
            return null;
        }

        return stdout;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double ParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static long ParseLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
}

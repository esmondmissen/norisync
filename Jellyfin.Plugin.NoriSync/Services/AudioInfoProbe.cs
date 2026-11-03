using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Extracts exact decode metadata (playable frame count, encoder delay/padding, moov position)
/// for an audio file by combining ffprobe with container-specific header parsing. ffprobe alone
/// only yields a rounded duration; the container headers carry the exact sample count and the
/// encoder priming/padding the client needs for precise EOF detection and gapless playback.
/// </summary>
public class AudioInfoProbe
{
    /// <summary>
    /// Version tag for the parsing logic. Bumping it invalidates every cached <see cref="AudioInfoDto"/>
    /// so changed parsing rules take effect on the next request.
    /// </summary>
    public const string Version = "audioinfo-v1";

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<AudioInfoProbe> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioInfoProbe"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Jellyfin media encoder (provides the ffprobe binary path).</param>
    /// <param name="logger">Logger instance.</param>
    public AudioInfoProbe(IMediaEncoder mediaEncoder, ILogger<AudioInfoProbe> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Probes a single audio file. Returns <see langword="null"/> when ffprobe fails or no audio
    /// stream is present.
    /// </summary>
    /// <param name="sourcePath">Absolute path of the audio file.</param>
    /// <param name="durationTicks">Jellyfin's recorded duration in 100-ns ticks, echoed into the result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The extracted audio info, or <see langword="null"/> on failure.</returns>
    public async Task<AudioInfoDto?> ProbeAsync(string sourcePath, long durationTicks, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            throw new ArgumentException("sourcePath is required", nameof(sourcePath));
        }

        var json = await RunFfprobeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (json == null)
        {
            return null;
        }

        ProbeBasics? basics;
        try
        {
            basics = ParseFfprobe(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse ffprobe JSON for {Path}", sourcePath);
            return null;
        }

        if (basics == null)
        {
            _logger.LogWarning("No audio stream found by ffprobe for {Path}", sourcePath);
            return null;
        }

        var dto = new AudioInfoDto
        {
            Codec = basics.Codec,
            SampleRate = basics.SampleRate,
            Channels = basics.Channels,
            DurationTicks = durationTicks
        };

        var codec = basics.Codec.ToLowerInvariant();
        var isMp4Family = ContainsToken(basics.FormatName, "mp4")
            || ContainsToken(basics.FormatName, "m4a")
            || ContainsToken(basics.FormatName, "mov");

        // 1. iTunes/MP4-encoded streams (AAC, ALAC) carry an exact iTunSMPB tag.
        if (isMp4Family
            && basics.ITunSmpb != null
            && TryParseITunSmpb(basics.ITunSmpb, out var mp4Delay, out var mp4Padding, out var mp4Frames))
        {
            dto.EncoderDelay = mp4Delay;
            dto.EncoderPadding = mp4Padding;
            dto.TotalFrames = mp4Frames;
            dto.TotalFramesExact = true;
        }
        else if (codec == "mp3" && basics.SampleRate > 0
            && TryParseMp3(sourcePath, out var mp3Frames, out var mp3Delay, out var mp3Padding))
        {
            dto.EncoderDelay = mp3Delay;
            dto.EncoderPadding = mp3Padding;
            dto.TotalFrames = mp3Frames;
            dto.TotalFramesExact = true;
        }
        else if (IsLossless(codec) && basics.SampleRate > 0
            && basics.DurationTs > 0 && basics.TimeBaseDen == basics.SampleRate && basics.TimeBaseNum == 1)
        {
            // Lossless streams expose their exact sample count directly: the stream time base is
            // 1/sampleRate, so duration_ts is the sample count and there is no encoder delay/padding.
            dto.TotalFrames = basics.DurationTs;
            dto.EncoderDelay = 0;
            dto.EncoderPadding = 0;
            dto.TotalFramesExact = true;
        }
        else
        {
            // No cheap exact source (no Xing/iTunSMPB header, unusual container). Decode the whole
            // file and count samples — still exact, just more expensive. The result is cached, so
            // this runs at most once per file. Only if even that fails do we fall back to a
            // duration-based approximation, flagged inexact so the client doesn't treat it as a
            // hard EOF boundary.
            var counted = await CountSamplesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (counted is > 0)
            {
                dto.TotalFrames = counted.Value;
                dto.TotalFramesExact = true;
            }
            else
            {
                dto.TotalFrames = basics.SampleRate > 0 && basics.DurationSeconds > 0
                    ? (long)Math.Round(basics.DurationSeconds * basics.SampleRate, MidpointRounding.AwayFromZero)
                    : 0;
                dto.TotalFramesExact = false;
            }
        }

        if (isMp4Family)
        {
            dto.MoovAtFront = DetectMoovAtFront(sourcePath);
        }

        return dto;
    }

    /// <summary>
    /// Parses an iTunes <c>iTunSMPB</c> tag. The value is 12 space-separated hex fields; field 1
    /// is the encoder delay (priming samples), field 2 the padding, and field 3 the original
    /// (playable) sample count.
    /// </summary>
    /// <param name="tag">The raw tag value.</param>
    /// <param name="delay">The parsed encoder delay.</param>
    /// <param name="padding">The parsed encoder padding.</param>
    /// <param name="totalFrames">The parsed playable sample count.</param>
    /// <returns>True when the tag was parsed successfully.</returns>
    internal static bool TryParseITunSmpb(string tag, out int delay, out int padding, out long totalFrames)
    {
        delay = 0;
        padding = 0;
        totalFrames = 0;

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var fields = tag.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4)
        {
            return false;
        }

        if (TryParseHex(fields[1], out var d) && TryParseHex(fields[2], out var p) && TryParseHex(fields[3], out var f)
            && d <= int.MaxValue && p <= int.MaxValue)
        {
            delay = (int)d;
            padding = (int)p;
            totalFrames = (long)f;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses an MP3's Xing/Info + LAME header to recover the exact playable sample count and the
    /// encoder delay/padding. Operates on the head of the file (after any ID3v2 tag).
    /// </summary>
    /// <param name="path">The MP3 file path.</param>
    /// <param name="totalFrames">The exact playable sample count per channel.</param>
    /// <param name="delay">The encoder delay (priming) in samples.</param>
    /// <param name="padding">The encoder padding in samples.</param>
    /// <returns>True when a Xing/LAME header was found and parsed.</returns>
    internal static bool TryParseMp3(string path, out long totalFrames, out int delay, out int padding)
    {
        totalFrames = 0;
        delay = 0;
        padding = 0;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryParseMp3(stream, out totalFrames, out delay, out padding);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Stream overload of the MP3 Xing/LAME parser, exposed for testing.
    /// </summary>
    /// <param name="stream">A readable, seekable stream positioned at the start of the file.</param>
    /// <param name="totalFrames">The exact playable sample count per channel.</param>
    /// <param name="delay">The encoder delay (priming) in samples.</param>
    /// <param name="padding">The encoder padding in samples.</param>
    /// <returns>True when a Xing/LAME header was found and parsed.</returns>
    internal static bool TryParseMp3(Stream stream, out long totalFrames, out int delay, out int padding)
    {
        totalFrames = 0;
        delay = 0;
        padding = 0;

        // Skip an ID3v2 tag if present so we land on the first real MPEG frame.
        var header = new byte[10];
        if (!ReadExactly(stream, header, 10))
        {
            return false;
        }

        long frameStart = 0;
        if (header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3')
        {
            var size = SyncSafe(header[6], header[7], header[8], header[9]);
            var footer = (header[5] & 0x10) != 0 ? 10 : 0;
            frameStart = 10 + size + footer;
        }

        stream.Seek(frameStart, SeekOrigin.Begin);
        var frame = new byte[1024];
        var read = ReadUpTo(stream, frame, frame.Length);
        if (read < 4 || frame[0] != 0xFF || (frame[1] & 0xE0) != 0xE0)
        {
            return false;
        }

        var versionBits = (frame[1] >> 3) & 0x03; // 0=MPEG2.5, 2=MPEG2, 3=MPEG1
        var layerBits = (frame[1] >> 1) & 0x03;   // 1=Layer III
        var channelMode = (frame[3] >> 6) & 0x03; // 3=mono
        if (layerBits != 0x01)
        {
            return false;
        }

        var isMpeg1 = versionBits == 0x03;
        var isMono = channelMode == 0x03;
        var samplesPerFrame = isMpeg1 ? 1152 : 576;

        // Xing/Info tag offset within the frame depends on MPEG version and channel mode.
        var xingOffset = isMpeg1
            ? (isMono ? 21 : 36)
            : (isMono ? 13 : 21);
        if (read < xingOffset + 8)
        {
            return false;
        }

        var tagId = Encoding.ASCII.GetString(frame, xingOffset, 4);
        if (tagId != "Xing" && tagId != "Info")
        {
            return false;
        }

        var pos = xingOffset + 4;
        var flags = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(pos));
        pos += 4;

        long frameCount = 0;
        if ((flags & 0x1) != 0)
        {
            if (read < pos + 4)
            {
                return false;
            }

            frameCount = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(pos));
            pos += 4;
        }
        else
        {
            return false; // No frame count → cannot compute an exact length.
        }

        if ((flags & 0x2) != 0)
        {
            pos += 4; // bytes field
        }

        if ((flags & 0x4) != 0)
        {
            pos += 100; // TOC
        }

        if ((flags & 0x8) != 0)
        {
            pos += 4; // quality field
        }

        // pos now points at the LAME tag (9-byte version string). Delay/padding are 3 bytes at
        // offset 21 within the LAME tag: delay = 12 bits, padding = 12 bits.
        var delayOffset = pos + 21;
        if (read < delayOffset + 3)
        {
            return false;
        }

        var b0 = frame[delayOffset];
        var b1 = frame[delayOffset + 1];
        var b2 = frame[delayOffset + 2];
        delay = (b0 << 4) | (b1 >> 4);
        padding = ((b1 & 0x0F) << 8) | b2;

        var rawSamples = frameCount * samplesPerFrame;
        totalFrames = rawSamples - delay - padding;
        if (totalFrames < 0)
        {
            totalFrames = 0;
        }

        return true;
    }

    /// <summary>
    /// Walks the top-level MP4 boxes to determine whether <c>moov</c> precedes <c>mdat</c>
    /// ("faststart"). Returns <see langword="null"/> when neither box is found or the structure
    /// cannot be parsed.
    /// </summary>
    /// <param name="path">The MP4/M4A file path.</param>
    /// <returns>True if moov is before mdat, false if after, null if undetermined.</returns>
    internal static bool? DetectMoovAtFront(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return DetectMoovAtFront(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stream overload of the MP4 box walker, exposed for testing.
    /// </summary>
    /// <param name="stream">A readable, seekable stream positioned at the start of the file.</param>
    /// <returns>True if moov is before mdat, false if after, null if undetermined.</returns>
    internal static bool? DetectMoovAtFront(Stream stream)
    {
        var box = new byte[16];
        long offset = 0;
        var length = stream.Length;

        for (var i = 0; i < 64 && offset + 8 <= length; i++)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            if (!ReadExactly(stream, box, 8))
            {
                return null;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(0));
            var type = Encoding.ASCII.GetString(box, 4, 4);

            if (size == 1)
            {
                // 64-bit largesize follows the 8-byte header.
                if (!ReadExactly(stream, box, 8))
                {
                    return null;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(box.AsSpan(0));
            }
            else if (size == 0)
            {
                // Box extends to EOF — it is the last box.
                size = length - offset;
            }

            if (type == "moov")
            {
                return true;
            }

            if (type == "mdat")
            {
                return false;
            }

            if (size < 8)
            {
                return null; // Malformed; bail rather than loop.
            }

            offset += size;
        }

        return null;
    }

    private static bool IsLossless(string codec)
    {
        if (codec.StartsWith("pcm_", StringComparison.Ordinal))
        {
            return true;
        }

        return codec switch
        {
            "flac" => true,
            "alac" => true,
            "wavpack" => true,
            "tta" => true,
            "ape" => true,
            "wmalossless" => true,
            _ => false
        };
    }

    private static bool ContainsToken(string formatName, string token)
        => formatName.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseHex(string value, out ulong result)
        => ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);

    private static int SyncSafe(byte b0, byte b1, byte b2, byte b3)
        => ((b0 & 0x7F) << 21) | ((b1 & 0x7F) << 14) | ((b2 & 0x7F) << 7) | (b3 & 0x7F);

    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        => ReadUpTo(stream, buffer, count) == count;

    private static int ReadUpTo(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = stream.Read(buffer, total, count - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private static ProbeBasics? ParseFfprobe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? audio = null;
        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.TryGetProperty("codec_type", out var type)
                && string.Equals(type.GetString(), "audio", StringComparison.Ordinal))
            {
                audio = stream;
                break;
            }
        }

        if (audio == null)
        {
            return null;
        }

        var s = audio.Value;
        var result = new ProbeBasics
        {
            Codec = GetString(s, "codec_name") ?? string.Empty,
            SampleRate = ParseInt(GetString(s, "sample_rate")),
            Channels = s.TryGetProperty("channels", out var ch) && ch.ValueKind == JsonValueKind.Number ? ch.GetInt32() : 0
        };

        if (s.TryGetProperty("duration_ts", out var dts) && dts.ValueKind == JsonValueKind.Number)
        {
            result.DurationTs = dts.GetInt64();
        }

        var timeBase = GetString(s, "time_base");
        if (!string.IsNullOrEmpty(timeBase))
        {
            var parts = timeBase.Split('/');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var den))
            {
                result.TimeBaseNum = num;
                result.TimeBaseDen = den;
            }
        }

        result.DurationSeconds = ParseDouble(GetString(s, "duration"));

        if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object)
        {
            result.FormatName = GetString(format, "format_name") ?? string.Empty;
            if (result.DurationSeconds <= 0)
            {
                result.DurationSeconds = ParseDouble(GetString(format, "duration"));
            }

            result.ITunSmpb = GetTag(format, "iTunSMPB");
        }

        result.ITunSmpb ??= GetTag(s, "iTunSMPB");

        return result;
    }

    private static string? GetTag(JsonElement element, string tagName)
    {
        if (!element.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var tag in tags.EnumerateObject())
        {
            if (string.Equals(tag.Name, tagName, StringComparison.OrdinalIgnoreCase))
            {
                return tag.Value.GetString();
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static double ParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private async Task<string?> RunFfprobeAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var args = string.Create(
            CultureInfo.InvariantCulture,
            $"-v error -print_format json -show_streams -show_format \"{sourcePath}\"");
        return await RunFfprobeAsync(args, sourcePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fully decodes the first audio stream and returns the exact number of samples per channel
    /// (post-gapless), or <see langword="null"/> when the count is unavailable. Expensive — it
    /// reads and decodes the whole file — so callers use it only when no header-based exact count
    /// is available.
    /// </summary>
    private async Task<long?> CountSamplesAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var args = string.Create(
            CultureInfo.InvariantCulture,
            $"-v error -select_streams a:0 -count_samples -show_entries stream=nb_read_samples -print_format json \"{sourcePath}\"");

        var json = await RunFfprobeAsync(args, sourcePath, cancellationToken).ConfigureAwait(false);
        if (json == null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var value = GetString(stream, "nb_read_samples");
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var samples)
                        && samples > 0)
                    {
                        return samples;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse ffprobe sample-count JSON for {Path}", sourcePath);
        }

        return null;
    }

    private async Task<string?> RunFfprobeAsync(string args, string sourcePath, CancellationToken cancellationToken)
    {
        var ffprobe = _mediaEncoder.ProbePath;
        if (string.IsNullOrEmpty(ffprobe))
        {
            _logger.LogWarning("ffprobe binary path is not available from IMediaEncoder");
            return null;
        }

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
            _logger.LogWarning("Failed to launch ffprobe for {Path}", sourcePath);
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            _logger.LogWarning("ffprobe exited with {Code} for {Path}: {Stderr}", process.ExitCode, sourcePath, stderr);
            return null;
        }

        return stdout;
    }

    private sealed class ProbeBasics
    {
        public string Codec { get; set; } = string.Empty;

        public string FormatName { get; set; } = string.Empty;

        public int SampleRate { get; set; }

        public int Channels { get; set; }

        public long DurationTs { get; set; }

        public int TimeBaseNum { get; set; }

        public int TimeBaseDen { get; set; }

        public double DurationSeconds { get; set; }

        public string? ITunSmpb { get; set; }
    }
}

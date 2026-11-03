using Jellyfin.Plugin.NoriSync.Services;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Tests for <see cref="ArtworkTranscoder"/>'s pure logic: alpha pixel-format detection and the
/// ffmpeg argument construction. The ffmpeg invocation itself is not exercised here (it needs the
/// server's encoder binary).
/// </summary>
public sealed class ArtworkTranscoderTests
{
    /// <summary>
    /// Alpha-carrying pixel formats must be detected; opaque and unknown ones must not. Notably
    /// <c>gray</c> contains the letter 'a' but has no alpha.
    /// </summary>
    /// <param name="pixelFormat">The ffprobe <c>pix_fmt</c> value.</param>
    /// <param name="expected">Whether the format carries alpha.</param>
    [Theory]
    [InlineData("yuva420p", true)]
    [InlineData("yuva444p10le", true)]
    [InlineData("rgba", true)]
    [InlineData("bgra", true)]
    [InlineData("argb", true)]
    [InlineData("gbrap", true)]
    [InlineData("ya8", true)]
    [InlineData("yuv420p", false)]
    [InlineData("yuv444p10le", false)]
    [InlineData("gray", false)]
    [InlineData("rgb24", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void HasAlpha_DetectsAlphaPixelFormats(string? pixelFormat, bool expected)
    {
        Assert.Equal(expected, ArtworkTranscoder.HasAlpha(pixelFormat));
    }

    /// <summary>
    /// The transcode command must scale to the short-edge cap, enforce the rate ceiling, encode
    /// H.264, and land faststart MP4 with audio stripped.
    /// </summary>
    [Fact]
    public void BuildTranscodeArguments_IncludesScaleRateAndFaststart()
    {
        var args = ArtworkTranscoder.BuildTranscodeArguments("/in/file.webm", "/out/file.mp4", 1080, 8000);

        Assert.Contains(
            "scale=w='trunc(min(iw*1080/min(iw,ih),iw)/2)*2':h='trunc(min(ih*1080/min(iw,ih),ih)/2)*2'",
            args,
            System.StringComparison.Ordinal);
        Assert.Contains("-maxrate 8000k -bufsize 16000k", args, System.StringComparison.Ordinal);
        Assert.Contains("-c:v libx264", args, System.StringComparison.Ordinal);
        Assert.Contains("-an", args, System.StringComparison.Ordinal);
        Assert.Contains("-movflags +faststart", args, System.StringComparison.Ordinal);
        Assert.Contains("-f mp4 \"/out/file.mp4\"", args, System.StringComparison.Ordinal);
        Assert.Contains("\"/in/file.webm\"", args, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A disabled resolution cap must not stop a bitrate-triggered re-encode: the scale filter
    /// falls back to a no-op cap (still enforcing even dimensions for yuv420p H.264), and no rate
    /// ceiling is emitted when the bitrate cap is disabled.
    /// </summary>
    [Fact]
    public void BuildTranscodeArguments_DisabledCaps_OmitOrRelaxTheirArgs()
    {
        var args = ArtworkTranscoder.BuildTranscodeArguments("/in/a.mp4", "/out/b.mp4", 0, 0);

        Assert.Contains(
            "scale=w='trunc(min(iw*16384/min(iw,ih),iw)/2)*2':h='trunc(min(ih*16384/min(iw,ih),ih)/2)*2'",
            args,
            System.StringComparison.Ordinal);
        Assert.DoesNotContain("-maxrate", args, System.StringComparison.Ordinal);
        Assert.DoesNotContain("-bufsize", args, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The scale filter is interpolated into <c>-vf {filter}</c> and .NET splits the argument string
    /// on whitespace, so a single stray space would hand ffmpeg two tokens and break every encode.
    /// </summary>
    [Fact]
    public void BuildTranscodeArguments_ScaleFilterIsOneToken()
    {
        var args = ArtworkTranscoder.BuildTranscodeArguments("/in/a.mp4", "/out/b.mp4", 720, 0);

        var start = args.IndexOf("-vf ", System.StringComparison.Ordinal) + 4;
        var filter = args[start..args.IndexOf(' ', start)];

        // The whole filter, quotes and all, has to survive as one token.
        Assert.StartsWith("scale=w=", filter, System.StringComparison.Ordinal);
        Assert.EndsWith("*2'", filter, System.StringComparison.Ordinal);
        Assert.Equal(2, filter.Split("*2'").Length - 1);
    }

    /// <summary>
    /// The dimensions the scale filter produces, asserted as arithmetic because no ffmpeg binary is
    /// available here. A step names the short edge, so the same number yields a different rectangle
    /// per shape — and never a larger one than the source.
    /// </summary>
    /// <param name="cap">The short-edge cap.</param>
    /// <param name="width">The source width.</param>
    /// <param name="height">The source height.</param>
    /// <param name="expectedWidth">The expected output width.</param>
    /// <param name="expectedHeight">The expected output height.</param>
    [Theory]
    // 16:9 lands on the full HD frame the "1080p" label promises.
    [InlineData(1080, 3840, 2160, 1920, 1080)]
    // 3:4 and 1:1 covers.
    [InlineData(1080, 2048, 2732, 1080, 1440)]
    [InlineData(1080, 2048, 2048, 1080, 1080)]
    // A source already at the cap is untouched.
    [InlineData(1080, 1920, 1080, 1920, 1080)]
    // Smaller than the cap: never upscaled.
    [InlineData(1080, 800, 600, 800, 600)]
    // Odd arithmetic floors to even rather than growing the frame.
    [InlineData(720, 1001, 1335, 720, 960)]
    // The no-cap sentinel only evens the dimensions.
    [InlineData(0, 1081, 1441, 1080, 1440)]
    public void PredictScaledSize_PutsTheShortEdgeOnTheCapAndNeverUpscales(
        int cap, int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), ArtworkTranscoder.PredictScaledSize(cap, width, height));
    }

    /// <summary>
    /// Across shapes and caps the filter must hold its invariants: the short edge lands on the cap
    /// (or the source, when smaller), both dimensions stay even for yuv420p, nothing grows, and the
    /// aspect ratio survives.
    /// </summary>
    [Fact]
    public void PredictScaledSize_HoldsItsInvariants()
    {
        int[][] sources =
        {
            new[] { 3840, 2160 }, new[] { 2048, 2732 }, new[] { 2048, 2048 }, new[] { 1920, 1080 },
            new[] { 1001, 1335 }, new[] { 800, 600 }, new[] { 640, 640 }, new[] { 2732, 2048 }
        };

        foreach (var cap in ArtworkSizeLadder.Steps)
        {
            foreach (var source in sources)
            {
                var (w, h) = ArtworkTranscoder.PredictScaledSize(cap, source[0], source[1]);

                Assert.Equal(0, w % 2);
                Assert.Equal(0, h % 2);
                Assert.True(w <= source[0] && h <= source[1], $"{source[0]}x{source[1]} @{cap} grew to {w}x{h}");

                var expectedShort = System.Math.Min(cap, System.Math.Min(source[0], source[1]));
                Assert.InRange(System.Math.Min(w, h), expectedShort - 1, expectedShort);

                var sourceRatio = (double)source[0] / source[1];
                Assert.InRange((double)w / h, sourceRatio * 0.99, sourceRatio * 1.01);
            }
        }
    }

    /// <summary>
    /// The remote fetch command must whitelist the HLS protocols (so an <c>.m3u8</c> input can follow
    /// its https segments), scale to the target short edge, encode H.264, and land faststart MP4.
    /// </summary>
    [Fact]
    public void BuildFetchArguments_WhitelistsHlsProtocolsAndReEncodesToFaststart()
    {
        var args = ArtworkTranscoder.BuildFetchArguments("https://cdn.example/cover.m3u8", "/out/file.mp4", 1080, 8000);

        Assert.Contains("-protocol_whitelist file,http,https,tcp,tls,crypto", args, System.StringComparison.Ordinal);
        Assert.Contains("-i \"https://cdn.example/cover.m3u8\"", args, System.StringComparison.Ordinal);
        Assert.Contains(
            "scale=w='trunc(min(iw*1080/min(iw,ih),iw)/2)*2':h='trunc(min(ih*1080/min(iw,ih),ih)/2)*2'",
            args,
            System.StringComparison.Ordinal);
        Assert.Contains("-maxrate 8000k -bufsize 16000k", args, System.StringComparison.Ordinal);
        Assert.Contains("-c:v libx264", args, System.StringComparison.Ordinal);
        Assert.Contains("-an", args, System.StringComparison.Ordinal);
        Assert.Contains("-movflags +faststart", args, System.StringComparison.Ordinal);
        Assert.Contains("-f mp4 \"/out/file.mp4\"", args, System.StringComparison.Ordinal);

        // An HLS master playlist exposes one video stream per rendition, so pinning "0:v:0" would
        // take the first-listed (lowest-quality) one. Default stream selection picks the largest.
        Assert.DoesNotContain("-map", args, System.StringComparison.Ordinal);

        // A playlist without #EXT-X-ENDLIST reads as live, where ffmpeg would otherwise start three
        // segments from the end and capture a fragment of the loop.
        Assert.Contains("-live_start_index 0", args, System.StringComparison.Ordinal);

        // Apple's CDN drops kept-alive connections mid-response, which shows up as corrupt packets.
        Assert.Contains("-http_persistent 0", args, System.StringComparison.Ordinal);
        Assert.Contains("-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5", args, System.StringComparison.Ordinal);

        // Recoverable segment errors are exactly what shortens the output, so they must not be
        // filtered out of the log.
        Assert.Contains("-loglevel warning", args, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The HLS-only input options must be omitted for a direct MP4 source: ffmpeg exits fatally on an
    /// input option that no demuxer or protocol consumes, so passing them would break every non-HLS
    /// motion cover. The http reconnect options still apply, since the protocol consumes those.
    /// </summary>
    [Fact]
    public void BuildFetchArguments_DirectMp4Source_OmitsHlsOnlyOptions()
    {
        var args = ArtworkTranscoder.BuildFetchArguments("https://cdn.example/cover.mp4", "/out/file.mp4", 1080, 8000);

        Assert.DoesNotContain("-http_persistent", args, System.StringComparison.Ordinal);
        Assert.DoesNotContain("-live_start_index", args, System.StringComparison.Ordinal);
        Assert.Contains("-reconnect 1", args, System.StringComparison.Ordinal);
        Assert.Contains("-i \"https://cdn.example/cover.mp4\"", args, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The copy fetch must not re-encode: the whole point is to store Apple's own encode when the
    /// resolved rendition is already the size we want. It still needs the HLS/http input options and
    /// faststart, since the source is a playlist and the output is streamed.
    /// </summary>
    [Fact]
    public void BuildFetchCopyArguments_CopiesTheStreamWithFaststart()
    {
        var args = ArtworkTranscoder.BuildFetchCopyArguments("https://cdn.example/cover.m3u8", "/out/file.mp4", isHevc: false);

        Assert.Contains("-c:v copy", args, System.StringComparison.Ordinal);
        Assert.DoesNotContain("libx264", args, System.StringComparison.Ordinal);
        Assert.DoesNotContain("-vf", args, System.StringComparison.Ordinal);
        Assert.Contains("-movflags +faststart", args, System.StringComparison.Ordinal);
        Assert.Contains("-http_persistent 0", args, System.StringComparison.Ordinal);
        Assert.Contains("-an", args, System.StringComparison.Ordinal);

        // hvc1 tagging is HEVC-only; forcing it on an H.264 stream would mislabel the sample entry.
        Assert.DoesNotContain("-tag:v", args, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// ffmpeg defaults an HEVC sample entry to <c>hev1</c> when muxing to MP4, which AVPlayer and
    /// QuickTime refuse. Copying HEVC has to tag it <c>hvc1</c>.
    /// </summary>
    [Fact]
    public void BuildFetchCopyArguments_TagsHevcAsHvc1()
    {
        var args = ArtworkTranscoder.BuildFetchCopyArguments("https://cdn.example/cover.m3u8", "/out/file.mp4", isHevc: true);

        Assert.Contains("-tag:v hvc1", args, System.StringComparison.Ordinal);
        Assert.Contains("-c:v copy", args, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-http source (a staged local file) gets neither set: the http protocol is not involved,
    /// and an unconsumed reconnect option would be just as fatal.
    /// </summary>
    [Fact]
    public void BuildFetchArguments_LocalFileSource_OmitsNetworkOptions()
    {
        var args = ArtworkTranscoder.BuildFetchArguments("/staged/cover.mp4", "/out/file.mp4", 1080, 8000);

        Assert.DoesNotContain("-reconnect", args, System.StringComparison.Ordinal);
        Assert.DoesNotContain("-http_persistent", args, System.StringComparison.Ordinal);
        Assert.DoesNotContain("-live_start_index", args, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The faststart remux must copy streams (no re-encode) and move <c>moov</c> to the front.
    /// </summary>
    [Fact]
    public void BuildRemuxArguments_CopiesStreamsWithFaststart()
    {
        var args = ArtworkTranscoder.BuildRemuxArguments("/in/a.mp4", "/out/b.mp4");

        Assert.Contains("-c copy", args, System.StringComparison.Ordinal);
        Assert.Contains("-movflags +faststart", args, System.StringComparison.Ordinal);
        Assert.DoesNotContain("libx264", args, System.StringComparison.Ordinal);
    }
}

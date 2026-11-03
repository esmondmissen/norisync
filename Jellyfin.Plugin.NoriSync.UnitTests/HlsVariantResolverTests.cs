using System;
using Jellyfin.Plugin.NoriSync.Services;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Tests for <see cref="HlsVariantResolver.SelectBestVariant"/>, which narrows an Apple motion-cover
/// master playlist to the one rendition ffmpeg should download. Picking wrong costs quality; failing
/// to recognize a master (or mistaking a media playlist for one) must fall back to the original URL
/// rather than break the fetch.
/// </summary>
public sealed class HlsVariantResolverTests
{
    private static readonly Uri Master = new("https://mvod.itunes.apple.com/itunes-assets/HLSVideo211/v4/ca/ea/71/P1420473656_default.m3u8");

    // The portrait rendition ladder Apple actually serves for a motion cover, trimmed to one entry
    // per resolution and with the real dimensions kept intact.
    private const string AppleLadder = """
    #EXTM3U
    #EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=264258,_AVG-BANDWIDTH=264258,BANDWIDTH=303491,CODECS="avc1.64001f",RESOLUTION=310x414
    P1420473656_Anull_video_gr203_sdr_310x414.m3u8
    #EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=1140244,_AVG-BANDWIDTH=1140244,BANDWIDTH=1237011,CODECS="avc1.64001f",RESOLUTION=486x648
    P1420473656_Anull_video_gr220_sdr_486x648.m3u8
    #EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=2132712,_AVG-BANDWIDTH=2132712,BANDWIDTH=2290757,CODECS="avc1.64001f",RESOLUTION=664x886
    P1420473656_Anull_video_gr240_sdr_664x886.m3u8
    #EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=2849560,_AVG-BANDWIDTH=2849560,BANDWIDTH=3054347,CODECS="avc1.640020",RESOLUTION=830x1106
    P1420473656_Anull_video_gr250_sdr_830x1106.m3u8
    #EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=3899176,_AVG-BANDWIDTH=3899176,BANDWIDTH=4142267,CODECS="avc1.640020",RESOLUTION=830x1108
    P1420473656_Anull_video_gr260_sdr_830x1108.m3u8
    #EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=6168080,_AVG-BANDWIDTH=6168080,BANDWIDTH=6500000,CODECS="avc1.640028",RESOLUTION=1078x1438
    P1420473656_Anull_video_gr280_sdr_1078x1438.m3u8
    #EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=9328259,_AVG-BANDWIDTH=9328259,BANDWIDTH=9800000,CODECS="avc1.640028",RESOLUTION=1080x1440
    P1420473656_Anull_video_gr290_sdr_1080x1440.m3u8
    #EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=14000000,_AVG-BANDWIDTH=14000000,BANDWIDTH=15000000,CODECS="avc1.640033",RESOLUTION=2048x2732
    P1420473656_Anull_video_gr350_sdr_2048x2732.m3u8
    """;

    /// <summary>
    /// The smallest rendition whose short edge still covers the target is taken — 1080x1440 for a
    /// 1080 target, Apple's own "1080p" rung — not the 2048x2732 top of the ladder, whose extra
    /// pixels the downscale would only throw away. Its relative URI is resolved against the master's
    /// own URL.
    /// </summary>
    [Fact]
    public void SelectBestVariant_PicksSmallestRenditionCoveringTarget()
    {
        var best = HlsVariantResolver.SelectBestVariant(AppleLadder, Master, 1080);

        Assert.NotNull(best);
        Assert.Equal(
            "https://mvod.itunes.apple.com/itunes-assets/HLSVideo211/v4/ca/ea/71/P1420473656_Anull_video_gr290_sdr_1080x1440.m3u8",
            best!.Url);

        // The fetch decides copy-vs-re-encode from these without opening the rendition first.
        Assert.Equal(1080, best.ShortEdge);
        Assert.Equal("avc1.640028", best.Codecs);
        Assert.False(best.IsHevc);
    }

    /// <summary>
    /// Apple publishes every rung twice, H.264 and HEVC. Copying HEVC into MP4 needs the <c>hvc1</c>
    /// tag, so the codec has to survive parsing — including the quoted form, whose value may itself
    /// contain a comma and so cannot be read as "up to the next separator".
    /// </summary>
    [Fact]
    public void SelectBestVariant_ReadsQuotedCodecs_AndDetectsHevc()
    {
        // Sized to cover the target rather than fall back to it, so this stays a test of codec
        // parsing and not of the largest-available fallback.
        const string Playlist = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=3138330,CODECS="hvc1.2.20000000.L123.B0,mp4a.40.2",RESOLUTION=1080x1440
        hevc.m3u8
        """;

        var best = HlsVariantResolver.SelectBestVariant(Playlist, Master, 1080);

        Assert.NotNull(best);
        Assert.Equal("hvc1.2.20000000.L123.B0,mp4a.40.2", best!.Codecs);
        Assert.True(best.IsHevc);
    }

    /// <summary>
    /// A target no rendition reaches falls back to the largest available rather than picking
    /// something small and upscaling it.
    /// </summary>
    [Fact]
    public void SelectBestVariant_FallsBackToLargest_WhenNoneReachesTarget()
        => Assert.EndsWith(
            "_2048x2732.m3u8",
            HlsVariantResolver.SelectBestVariant(AppleLadder, Master, 4000)?.Url,
            StringComparison.Ordinal);

    /// <summary>
    /// With the resolution cap disabled there is no target to satisfy, so the largest rendition wins.
    /// </summary>
    [Fact]
    public void SelectBestVariant_TakesLargest_WhenNoTargetGiven()
        => Assert.EndsWith(
            "_2048x2732.m3u8",
            HlsVariantResolver.SelectBestVariant(AppleLadder, Master, 0)?.Url,
            StringComparison.Ordinal);

    /// <summary>
    /// A small target still lands on a rendition at or above it, never below. 486x648 is short of
    /// 500 across, so the 664x886 rung is the smallest that covers it.
    /// </summary>
    [Fact]
    public void SelectBestVariant_HonoursASmallTarget()
        => Assert.EndsWith(
            "_664x886.m3u8",
            HlsVariantResolver.SelectBestVariant(AppleLadder, Master, 500)?.Url,
            StringComparison.Ordinal);

    /// <summary>
    /// A landscape ladder is read the same way: the short edge is the height, so a 1920x1080
    /// rendition covers a 1080 target but not an 1081 one. Apple only publishes square and tall
    /// covers, but the resolver is also used for whatever a self-hosted service returns.
    /// </summary>
    [Fact]
    public void SelectBestVariant_ReadsLandscapeRenditionsByTheirHeight()
    {
        const string Playlist = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=3000000,CODECS="avc1.640028",RESOLUTION=1920x1080
        hd.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=1000000,CODECS="avc1.64001f",RESOLUTION=1280x720
        sd.m3u8
        """;

        Assert.EndsWith("hd.m3u8", HlsVariantResolver.SelectBestVariant(Playlist, Master, 1080)?.Url, StringComparison.Ordinal);
        Assert.EndsWith("sd.m3u8", HlsVariantResolver.SelectBestVariant(Playlist, Master, 720)?.Url, StringComparison.Ordinal);

        // Nothing reaches 1081 across the short edge, so the largest available wins.
        Assert.EndsWith("hd.m3u8", HlsVariantResolver.SelectBestVariant(Playlist, Master, 1081)?.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// An absolute rendition URI is used as-is rather than being re-based on the master.
    /// </summary>
    [Fact]
    public void SelectBestVariant_KeepsAbsoluteRenditionUri()
    {
        const string Playlist = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=4200000,RESOLUTION=1080x1440
        https://cdn.example/other/host/1080.m3u8
        """;

        Assert.Equal("https://cdn.example/other/host/1080.m3u8", HlsVariantResolver.SelectBestVariant(Playlist, Master, 1080)?.Url);
    }

    /// <summary>
    /// <c>AVERAGE-BANDWIDTH</c> must not be read as <c>BANDWIDTH</c>: the tie-break would otherwise
    /// compare the wrong numbers when renditions share a resolution.
    /// </summary>
    [Fact]
    public void SelectBestVariant_DoesNotConfuseAverageBandwidthWithBandwidth()
    {
        const string Playlist = """
        #EXTM3U
        #EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=9000000,BANDWIDTH=1000000,RESOLUTION=1080x1440
        low.m3u8
        #EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=1000000,BANDWIDTH=8000000,RESOLUTION=1080x1440
        high.m3u8
        """;

        Assert.EndsWith("/high.m3u8", HlsVariantResolver.SelectBestVariant(Playlist, Master, 1080)?.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no <c>RESOLUTION</c> to compare, the highest declared bandwidth wins.
    /// </summary>
    [Fact]
    public void SelectBestVariant_FallsBackToBandwidth_WhenResolutionAbsent()
    {
        const string Playlist = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=800000
        small.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=5000000
        big.m3u8
        """;

        Assert.EndsWith("/big.m3u8", HlsVariantResolver.SelectBestVariant(Playlist, Master, 1080)?.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// A media playlist carries no rendition list, so there is nothing to narrow and the caller must
    /// keep the URL it already had.
    /// </summary>
    [Fact]
    public void SelectBestVariant_ReturnsNull_ForMediaPlaylist()
    {
        const string Playlist = """
        #EXTM3U
        #EXT-X-TARGETDURATION:6
        #EXT-X-MAP:URI="init.mp4"
        #EXTINF:6.0,
        segment0.mp4
        #EXT-X-ENDLIST
        """;

        Assert.Null(HlsVariantResolver.SelectBestVariant(Playlist, Master, 1080)?.Url);
    }

    /// <summary>
    /// Tag lines between the rendition declaration and its URI must not be taken for the URI.
    /// </summary>
    [Fact]
    public void SelectBestVariant_SkipsInterveningTagLines()
    {
        const string Playlist = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=4200000,RESOLUTION=1080x1440
        #EXT-X-SOMETHING-ELSE:1
        real.m3u8
        """;

        Assert.EndsWith("/real.m3u8", HlsVariantResolver.SelectBestVariant(Playlist, Master, 1080)?.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// Windows-style line endings are common in served playlists; the trailing carriage return must
    /// not end up in the resolved URL.
    /// </summary>
    [Fact]
    public void SelectBestVariant_HandlesCarriageReturns()
    {
        const string Playlist = "#EXTM3U\r\n#EXT-X-STREAM-INF:BANDWIDTH=4200000,RESOLUTION=1080x1440\r\nbest.m3u8\r\n";

        Assert.EndsWith("/best.m3u8", HlsVariantResolver.SelectBestVariant(Playlist, Master, 1080)?.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// Garbage in yields no selection rather than an exception.
    /// </summary>
    [Fact]
    public void SelectBestVariant_ReturnsNull_ForEmptyOrNonPlaylistText()
    {
        Assert.Null(HlsVariantResolver.SelectBestVariant(string.Empty, Master, 1080)?.Url);
        Assert.Null(HlsVariantResolver.SelectBestVariant("not a playlist at all", Master, 1080)?.Url);
    }
}

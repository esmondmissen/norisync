using System.Text.Json;
using Jellyfin.Plugin.NoriSync.Services;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Tests for <see cref="AppleMusicArtworkClient.ParseMatch"/>, the tolerant extractor that pulls the
/// square/tall motion-cover URLs out of the service's search response. The parser is deliberately
/// shape-agnostic (the live response shape could not be captured), so these lock in the classification
/// and preference rules it relies on across a few plausible shapes.
/// </summary>
public sealed class AppleMusicArtworkClientTests
{
    /// <summary>
    /// Explicit <c>square</c>/<c>tall</c> objects, each with a nested <c>url</c>, map to the matching
    /// variant by their property name.
    /// </summary>
    [Fact]
    public void ParseMatch_ExplicitSquareAndTallObjects_MapsBoth()
    {
        const string Json = """
        { "square": { "url": "https://cdn.example/s.m3u8" },
          "tall":   { "url": "https://cdn.example/t.m3u8" } }
        """;
        using var doc = JsonDocument.Parse(Json);

        var match = AppleMusicArtworkClient.ParseMatch(doc.RootElement);

        Assert.NotNull(match);
        Assert.Equal("https://cdn.example/s.m3u8", match!.SquareUrl);
        Assert.Equal("https://cdn.example/t.m3u8", match.TallUrl);
    }

    /// <summary>
    /// An array of variant entries classified by an adjacent <c>type</c> descriptor (rather than the
    /// URL's own property name) is still resolved correctly.
    /// </summary>
    [Fact]
    public void ParseMatch_VariantArrayWithTypeDescriptor_ClassifiesByType()
    {
        const string Json = """
        { "artworks": [
            { "type": "square", "m3u8": "https://cdn.example/s.m3u8" },
            { "type": "tall",   "mp4":  "https://cdn.example/t.mp4" } ] }
        """;
        using var doc = JsonDocument.Parse(Json);

        var match = AppleMusicArtworkClient.ParseMatch(doc.RootElement);

        Assert.NotNull(match);
        Assert.Equal("https://cdn.example/s.m3u8", match!.SquareUrl);
        Assert.Equal("https://cdn.example/t.mp4", match.TallUrl);
    }

    /// <summary>
    /// When both an HLS and a direct MP4 exist for the same variant, the MP4 is preferred regardless
    /// of the order they appear in.
    /// </summary>
    [Fact]
    public void ParseMatch_BothHlsAndMp4ForVariant_PrefersMp4()
    {
        const string Json = """
        { "square": { "m3u8": "https://cdn.example/s.m3u8", "mp4": "https://cdn.example/s.mp4" } }
        """;
        using var doc = JsonDocument.Parse(Json);

        var match = AppleMusicArtworkClient.ParseMatch(doc.RootElement);

        Assert.NotNull(match);
        Assert.Equal("https://cdn.example/s.mp4", match!.SquareUrl);
    }

    /// <summary>
    /// A single unclassified video URL (no orientation hint) is treated as the square cover, since
    /// that is the standard default variant.
    /// </summary>
    [Fact]
    public void ParseMatch_SingleUnclassifiedUrl_TreatedAsSquare()
    {
        const string Json = """{ "url": "https://cdn.example/only.mp4" }""";
        using var doc = JsonDocument.Parse(Json);

        var match = AppleMusicArtworkClient.ParseMatch(doc.RootElement);

        Assert.NotNull(match);
        Assert.Equal("https://cdn.example/only.mp4", match!.SquareUrl);
        Assert.Null(match.TallUrl);
    }

    /// <summary>
    /// A <c>tall</c> descriptor wins over a <c>cover</c> hint on the same URL, so a portrait cover is
    /// never misfiled into the square slot.
    /// </summary>
    [Fact]
    public void ParseMatch_TallDescriptorWithCoverHint_ClassifiesAsTall()
    {
        const string Json = """
        { "artworks": [ { "type": "tall", "coverUrl": "https://cdn.example/t.mp4" } ] }
        """;
        using var doc = JsonDocument.Parse(Json);

        var match = AppleMusicArtworkClient.ParseMatch(doc.RootElement);

        Assert.NotNull(match);
        Assert.Equal("https://cdn.example/t.mp4", match!.TallUrl);
        Assert.Null(match.SquareUrl);
    }

    /// <summary>
    /// A response carrying only a static image (no <c>.mp4</c>/<c>.m3u8</c>) yields no match.
    /// </summary>
    [Fact]
    public void ParseMatch_OnlyStaticImage_ReturnsNull()
    {
        const string Json = """{ "images": [ { "url": "https://cdn.example/cover.jpg" } ] }""";
        using var doc = JsonDocument.Parse(Json);

        var match = AppleMusicArtworkClient.ParseMatch(doc.RootElement);

        Assert.Null(match);
    }

    /// <summary>
    /// An empty object yields no match rather than throwing.
    /// </summary>
    [Fact]
    public void ParseMatch_EmptyObject_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("{}");

        var match = AppleMusicArtworkClient.ParseMatch(doc.RootElement);

        Assert.Null(match);
    }
}

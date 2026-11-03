using Jellyfin.Plugin.NoriSync.Services;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Tests for <see cref="ArtworkShape"/>: the aspect-ratio classification that decides which album
/// slot a loop belongs in. The tolerance must treat a cover-shaped clip as square even when an odd
/// dimension was rounded to an even one, still catch a genuinely portrait clip, and stay silent
/// when the dimensions are unknown or the slot has no shape expectation.
/// </summary>
public sealed class ArtworkShapeTests
{
    [Theory]
    [InlineData(1080, 1920, true)]     // 9:16
    [InlineData(1000, 1334, true)]     // 3:4
    [InlineData(1000, 1101, true)]     // just past the tolerance
    [InlineData(1000, 1100, false)]    // exactly at the tolerance
    [InlineData(1000, 1000, false)]
    [InlineData(1920, 1080, false)]
    [InlineData(null, 1920, false)]
    [InlineData(1080, null, false)]
    [InlineData(0, 0, false)]
    public void IsTall_ClassifiesPortraitOnly(int? width, int? height, bool expected)
        => Assert.Equal(expected, ArtworkShape.IsTall(width, height));

    [Theory]
    [InlineData(1000, 1000, true)]
    [InlineData(1000, 1100, true)]     // within tolerance either way
    [InlineData(1100, 1000, true)]
    [InlineData(1000, 1101, false)]
    [InlineData(1920, 1080, false)]
    [InlineData(null, null, false)]
    public void IsSquare_AllowsToleranceInBothDirections(int? width, int? height, bool expected)
        => Assert.Equal(expected, ArtworkShape.IsSquare(width, height));

    [Theory]
    [InlineData("Tall", "Album", 1080, 1920, null)]
    [InlineData("Tall", "Album", 1000, 1000, "aspect_not_tall")]
    [InlineData("Tall", "Album", 1920, 1080, "aspect_not_tall")]
    [InlineData("tall", "album", 1000, 1000, "aspect_not_tall")]
    [InlineData("Primary", "Album", 1000, 1000, null)]
    [InlineData("Primary", "Album", 1080, 1920, "aspect_not_square")]
    [InlineData("Primary", "Album", 1920, 1080, "aspect_not_square")]
    [InlineData("Primary", "Artist", 1080, 1920, null)]
    [InlineData("Backdrop", "Album", 1920, 1080, null)]
    [InlineData("Logo", "Artist", 1000, 400, null)]
    [InlineData("Tall", "Album", null, null, null)]
    [InlineData("Primary", "Album", 1000, null, null)]
    public void GetMismatchWarning_WarnsOnlyWhenTheSlotExpectsAShapeItDidNotGet(
        string imageType,
        string itemType,
        int? width,
        int? height,
        string? expected)
        => Assert.Equal(expected, ArtworkShape.GetMismatchWarning(imageType, itemType, width, height));
}

using Jellyfin.Plugin.NoriSync.Services;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Tests for <see cref="ArtworkSizeLadder"/>, which quantizes a requested short edge to the fixed
/// set of sizes the server will cut. A step names the short edge — the "1080p" reading — so the
/// ladder is bounded by the master's narrow dimension whatever its orientation. Rounding must always
/// go up (a step below the request would be upscaled on screen), and anything at or above the master
/// must resolve to the master so no rendition is ever produced that is bigger than its source.
/// </summary>
public sealed class ArtworkSizeLadderTests
{
    [Theory]
    // Requests round up to the next step.
    [InlineData(100, 1080, 1080, 480)]
    [InlineData(480, 1080, 1080, 480)]
    [InlineData(481, 1080, 1080, 720)]
    [InlineData(720, 1080, 1080, 720)]
    // A tall master: the short edge is the width, and it is what bounds the ladder.
    [InlineData(720, 810, 1080, 720)]
    [InlineData(1000, 1200, 1600, 1080)]
    // A landscape master: the short edge is the height. These are the rows that tell the two
    // readings apart — under long-edge sizing every one of them would resolve to a step.
    [InlineData(1080, 1920, 1080, null)]
    [InlineData(1440, 1920, 1080, null)]
    [InlineData(720, 1280, 720, null)]
    [InlineData(720, 1920, 1080, 720)]
    [InlineData(1080, 3840, 2160, 1080)]
    // At or above the master there is nothing to cut.
    [InlineData(1080, 1080, 1080, null)]
    [InlineData(2000, 1080, 1080, null)]
    // A step that meets or exceeds the master is the master.
    [InlineData(1000, 1080, 1080, null)]
    // Above the ladder but below the master: the master is the closest thing available.
    [InlineData(2500, 3000, 4000, null)]
    // Never upscale a source smaller than the request.
    [InlineData(1080, 800, 600, null)]
    // Nothing requested, or a master of unknown or half-known size, means serve as-is.
    [InlineData(null, 1080, 1080, null)]
    [InlineData(0, 1080, 1080, null)]
    [InlineData(-10, 1080, 1080, null)]
    [InlineData(720, null, null, null)]
    [InlineData(720, 1080, null, null)]
    public void Resolve_RoundsUpAndNeverExceedsTheMaster(int? requested, int? masterWidth, int? masterHeight, int? expected)
        => Assert.Equal(expected, ArtworkSizeLadder.Resolve(requested, masterWidth, masterHeight));

    /// <summary>
    /// Sizing by the short edge means a master and its transpose resolve identically: a 3:4 cover and
    /// a 4:3 backdrop of the same pixel count get the same rendition size. This is the property the
    /// per-row cases above only sample.
    /// </summary>
    [Fact]
    public void Resolve_IsOrientationInvariant()
    {
        int[] requests = { 100, 480, 720, 1080, 1440, 2160, 3000 };
        int[][] masters = { new[] { 1920, 1080 }, new[] { 2048, 2732 }, new[] { 2048, 2048 }, new[] { 800, 600 } };

        foreach (var request in requests)
        {
            foreach (var master in masters)
            {
                Assert.Equal(
                    ArtworkSizeLadder.Resolve(request, master[0], master[1]),
                    ArtworkSizeLadder.Resolve(request, master[1], master[0]));
            }
        }
    }

    /// <summary>
    /// Only steps strictly below the master's short edge are worth offering — the master itself
    /// covers the rest.
    /// </summary>
    [Fact]
    public void AvailableFor_ListsOnlyStepsBelowTheMastersShortEdge()
    {
        Assert.Equal(new[] { 480, 720 }, ArtworkSizeLadder.AvailableFor(1080, 1080));

        // A 1200x1600 master is bounded by its 1200px width, so 1440 is out of reach even though the
        // master is 1600 tall.
        Assert.Equal(new[] { 480, 720, 1080 }, ArtworkSizeLadder.AvailableFor(1200, 1600));
        Assert.Equal(new[] { 480, 720, 1080, 1440, 2160 }, ArtworkSizeLadder.AvailableFor(2400, 3200));

        // Landscape masters are bounded by their height.
        Assert.Equal(new[] { 480, 720 }, ArtworkSizeLadder.AvailableFor(1920, 1080));
        Assert.Equal(new[] { 480, 720, 1080, 1440 }, ArtworkSizeLadder.AvailableFor(3840, 2160));
    }

    /// <summary>
    /// The offered steps do not depend on which way round the master is.
    /// </summary>
    [Fact]
    public void AvailableFor_IsOrientationInvariant()
    {
        Assert.Equal(ArtworkSizeLadder.AvailableFor(3840, 2160), ArtworkSizeLadder.AvailableFor(2160, 3840));
        Assert.Equal(ArtworkSizeLadder.AvailableFor(1200, 1600), ArtworkSizeLadder.AvailableFor(1600, 1200));
    }

    /// <summary>
    /// A master smaller than every step, or of unknown or half-known size, offers nothing to choose
    /// from.
    /// </summary>
    [Fact]
    public void AvailableFor_IsEmpty_WhenMasterIsTinyOrUnknown()
    {
        Assert.Empty(ArtworkSizeLadder.AvailableFor(400, 400));
        Assert.Empty(ArtworkSizeLadder.AvailableFor(null, null));
        Assert.Empty(ArtworkSizeLadder.AvailableFor(1920, null));
    }

    /// <summary>
    /// The ladder must stay ascending: <see cref="ArtworkSizeLadder.Resolve"/> returns the first step
    /// that covers the request, which is only the smallest such step if the list is ordered.
    /// </summary>
    [Fact]
    public void Steps_AreAscending()
    {
        var steps = ArtworkSizeLadder.Steps;
        for (var i = 1; i < steps.Count; i++)
        {
            Assert.True(steps[i] > steps[i - 1], $"step {steps[i]} must be greater than {steps[i - 1]}");
        }
    }

    /// <summary>
    /// A published rendition close enough to the target is stored as-is; one well past it is
    /// re-encoded rather than kept at several times the size that was asked for.
    /// </summary>
    /// <param name="sourceShortEdge">The candidate source's short edge.</param>
    /// <param name="targetShortEdge">The short edge being aimed for.</param>
    /// <param name="expected">Whether the source can be copied untouched.</param>
    [Theory]
    // Exactly on target, and within the overshoot allowance.
    [InlineData(1080, 1080, true)]
    [InlineData(1350, 1080, true)]
    // Just past it.
    [InlineData(1351, 1080, false)]
    // Apple's real tall ladder jumps 1080 -> 2048 across the short edge, far enough to re-encode.
    [InlineData(2048, 1200, false)]
    // No target: take whatever was offered.
    [InlineData(2048, 0, true)]
    [InlineData(-1, 0, true)]
    // A source that declared no size cannot be bounded except by re-encoding.
    [InlineData(-1, 1080, false)]
    [InlineData(0, 1080, false)]
    public void CanCopyAtSize_AllowsModestOvershootOnly(int sourceShortEdge, int targetShortEdge, bool expected)
        => Assert.Equal(expected, ArtworkSizeLadder.CanCopyAtSize(sourceShortEdge, targetShortEdge));

    /// <summary>
    /// The derived bitrate ceiling picks the tier covering the short edge, with tier boundaries
    /// inclusive, sizes above the ladder on the top tier, and zero (uncapped size) treated as the
    /// top tier too.
    /// </summary>
    /// <param name="shortEdge">The rendition's short edge in pixels.</param>
    /// <param name="expectedKbps">The expected bitrate ceiling in kbps.</param>
    [Theory]
    [InlineData(480, 4500)]
    [InlineData(481, 6500)]
    [InlineData(720, 6500)]
    [InlineData(1080, 11500)]
    [InlineData(1440, 17500)]
    [InlineData(2160, 30000)]
    [InlineData(4320, 30000)]
    [InlineData(0, 30000)]
    public void BitrateKbpsFor_MapsEdgeToTier(int shortEdge, int expectedKbps)
    {
        Assert.Equal(expectedKbps, ArtworkSizeLadder.BitrateKbpsFor(shortEdge));
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// The fixed set of sizes an animated-artwork master can be served down to. A step names the
/// video's <em>short</em> edge — the "1080p" reading, which holds in either orientation: a 16:9
/// loop at 1080 is 1920x1080 and a 3:4 cover is 1080x1440. Client requests are rounded up to a step
/// rather than honoured literally, because a derivative is a transcode plus a file on disk, so
/// arbitrary pixel values would grow the cache without bound and re-encode the same clip once per
/// distinct screen size in the wild.
/// </summary>
public static class ArtworkSizeLadder
{
    // How far past the target a source may sit and still be stored as-is by CanCopyAtSize.
    private const double MaxCopyOvershoot = 1.25;

    /// <summary>
    /// Gets the ladder steps, ascending. Chosen to bracket the surfaces artwork is actually drawn
    /// on: a list thumbnail, a phone-width cover, a phone full-bleed background, and a tablet one.
    /// </summary>
    public static IReadOnlyList<int> Steps { get; } = new ReadOnlyCollection<int>(new[] { 480, 720, 1080, 1440, 2160 });

    /// <summary>
    /// Resolves a requested short edge to the ladder step that should be served, or
    /// <see langword="null"/> to serve the master untouched — which is the answer when no size was
    /// asked for, the master's dimensions are unknown, or the request is at or above the master's
    /// own short edge. Never returns a step that would upscale.
    /// </summary>
    /// <param name="requestedShortEdge">The short edge that was asked for, if any.</param>
    /// <param name="masterWidth">The master's width in pixels, when known.</param>
    /// <param name="masterHeight">The master's height in pixels, when known.</param>
    /// <returns>The ladder step to serve, or <see langword="null"/> for the master.</returns>
    public static int? Resolve(int? requestedShortEdge, int? masterWidth, int? masterHeight)
    {
        if (requestedShortEdge is not int requested || requested <= 0)
        {
            return null;
        }

        // Both dimensions have to be known: with Math.Min a half-probed master would report a short
        // edge of zero and fall through as "unknown" anyway, so say so rather than implying it.
        if (masterWidth is not int width || masterHeight is not int height || width <= 0 || height <= 0)
        {
            return null;
        }

        var masterEdge = Math.Min(width, height);
        if (requested >= masterEdge)
        {
            return null;
        }

        foreach (var step in Steps)
        {
            if (step < requested)
            {
                continue;
            }

            // A step that meets or exceeds the master is just the master with extra work.
            return step < masterEdge ? step : null;
        }

        // Asked for more than the ladder covers but less than the master: the master is the closest
        // thing to what they want.
        return null;
    }

    /// <summary>
    /// The encoder's bitrate ceiling, in kilobits per second, for a rendition at the given step.
    /// Derived rather than configured: each tier carries a rate that is comfortable for looping
    /// H.264 cover art at that size, so quality is a single resolution choice. Because a step names
    /// the short edge, the same tier covers up to 1.78x more pixels for a 16:9 asset than for a
    /// square one, and the tiers are set for that widest case — this is a ceiling over CRF 23, so
    /// headroom the square and tall shapes never reach costs them nothing.
    /// </summary>
    /// <param name="shortEdge">The short edge of the rendition being encoded, in pixels. Zero or
    /// negative means uncapped size and gets the top tier's ceiling.</param>
    /// <returns>The bitrate ceiling in kbps.</returns>
    public static int BitrateKbpsFor(int shortEdge)
    {
        if (shortEdge <= 0)
        {
            return 30000;
        }

        return shortEdge switch
        {
            <= 480 => 4500,
            <= 720 => 6500,
            <= 1080 => 11500,
            <= 1440 => 17500,
            _ => 30000
        };
    }

    /// <summary>
    /// Whether a source already at <paramref name="sourceShortEdge"/> is close enough to the target
    /// to store as-is instead of re-encoding it. A published rendition ladder rarely lands exactly on
    /// the size asked for, and shaving a few percent off is not worth an encode — but a ladder whose
    /// rungs are far apart can offer something much larger, and that is when re-encoding earns its
    /// CPU. A source of unknown size can only be bounded by re-encoding.
    /// </summary>
    /// <param name="sourceShortEdge">The candidate source's short edge; zero or negative means unknown.</param>
    /// <param name="targetShortEdge">The short edge being aimed for; zero or negative means no target,
    /// so anything may be copied.</param>
    /// <returns>True when the source can be stored without re-encoding.</returns>
    public static bool CanCopyAtSize(int sourceShortEdge, int targetShortEdge)
    {
        if (targetShortEdge <= 0)
        {
            return true;
        }

        return sourceShortEdge > 0 && sourceShortEdge <= targetShortEdge * MaxCopyOvershoot;
    }

    /// <summary>
    /// Lists the steps worth requesting for a master of the given size — those strictly below its
    /// short edge. Advertised on the list endpoint so a client picks from what the server will
    /// actually produce instead of hard-coding the ladder.
    /// </summary>
    /// <param name="masterWidth">The master's width in pixels, when known.</param>
    /// <param name="masterHeight">The master's height in pixels, when known.</param>
    /// <returns>The usable steps, ascending; empty when the master is smaller than every step.</returns>
    public static IReadOnlyList<int> AvailableFor(int? masterWidth, int? masterHeight)
    {
        if (masterWidth is not int width || masterHeight is not int height || width <= 0 || height <= 0)
        {
            return Array.Empty<int>();
        }

        var masterEdge = Math.Min(width, height);
        var available = new List<int>();
        foreach (var step in Steps)
        {
            if (step < masterEdge)
            {
                available.Add(step);
            }
        }

        return available;
    }
}

using System;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Names the album animated-artwork slots by shape and classifies assets by aspect ratio. An album
/// carries two loops: a square one in the <c>Primary</c> slot (matching the cover) and a portrait
/// one in the <c>Tall</c> slot (for full-bleed now-playing surfaces). Shape is never enforced —
/// classification only drives an advisory upload warning and the one-time migration that re-slots
/// portrait loops stored before the tall slot existed.
/// </summary>
public static class ArtworkShape
{
    /// <summary>
    /// The image slot holding an album's portrait loop.
    /// </summary>
    public const string TallImageType = "Tall";

    /// <summary>
    /// The image slot holding an album's square loop, alongside the static cover.
    /// </summary>
    public const string SquareImageType = "Primary";

    /// <summary>
    /// How far the long edge may exceed the short edge before an asset stops counting as square.
    /// A tenth absorbs the rounding of an odd dimension up or down to an even one (H.264 yuv420p
    /// requires even dimensions) without letting a genuinely portrait clip pass as square.
    /// </summary>
    public const double SquareTolerance = 1.1;

    /// <summary>
    /// Returns whether the given dimensions describe a portrait asset — taller than square by more
    /// than <see cref="SquareTolerance"/>. Unknown or non-positive dimensions are not tall.
    /// </summary>
    /// <param name="width">The asset width in pixels, when known.</param>
    /// <param name="height">The asset height in pixels, when known.</param>
    /// <returns><see langword="true"/> when the asset is portrait.</returns>
    public static bool IsTall(int? width, int? height)
        => TryGetDimensions(width, height, out var w, out var h) && h > w * SquareTolerance;

    /// <summary>
    /// Returns whether the given dimensions describe a square asset, within
    /// <see cref="SquareTolerance"/>. Unknown or non-positive dimensions are not square.
    /// </summary>
    /// <param name="width">The asset width in pixels, when known.</param>
    /// <param name="height">The asset height in pixels, when known.</param>
    /// <returns><see langword="true"/> when the asset is square.</returns>
    public static bool IsSquare(int? width, int? height)
        => TryGetDimensions(width, height, out var w, out var h)
            && Math.Max(w, h) <= Math.Min(w, h) * SquareTolerance;

    /// <summary>
    /// Returns the advisory warning code for an asset whose shape does not match the slot it is
    /// being stored in — <c>"aspect_not_tall"</c> for a non-portrait asset in the tall slot,
    /// <c>"aspect_not_square"</c> for a non-square one in an album's primary slot — or
    /// <see langword="null"/> when the shape fits, the slot has no shape expectation, or the
    /// dimensions are unknown.
    /// </summary>
    /// <param name="imageType">The canonical image slot the asset is stored in.</param>
    /// <param name="itemType">The item kind, e.g. <c>"Album"</c> or <c>"Artist"</c>.</param>
    /// <param name="width">The asset width in pixels, when known.</param>
    /// <param name="height">The asset height in pixels, when known.</param>
    /// <returns>The warning code, or <see langword="null"/> when there is nothing to warn about.</returns>
    public static string? GetMismatchWarning(string imageType, string itemType, int? width, int? height)
    {
        if (!TryGetDimensions(width, height, out _, out _))
        {
            return null;
        }

        if (string.Equals(imageType, TallImageType, StringComparison.OrdinalIgnoreCase))
        {
            return IsTall(width, height) ? null : "aspect_not_tall";
        }

        // Only albums have a square expectation: an artist's primary loop is not tied to a cover.
        if (string.Equals(imageType, SquareImageType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(itemType, "Album", StringComparison.OrdinalIgnoreCase))
        {
            return IsSquare(width, height) ? null : "aspect_not_square";
        }

        return null;
    }

    private static bool TryGetDimensions(int? width, int? height, out int w, out int h)
    {
        w = width ?? 0;
        h = height ?? 0;
        return w > 0 && h > 0;
    }
}

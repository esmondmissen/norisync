namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A resolved animated-artwork match returned by the Apple Music animated-artwork service for a
/// given artist/album query. Each variant URL points at either a direct MP4 or an HLS <c>.m3u8</c>
/// playlist; ffmpeg reads both, so the fetch pipeline treats them uniformly.
/// </summary>
public sealed class AppleArtworkMatch
{
    /// <summary>
    /// Gets or sets the source URL of the square (1:1) motion cover, or <see langword="null"/> when
    /// the service reported none. Stored in the album's <c>Primary</c> slot.
    /// </summary>
    public string? SquareUrl { get; set; }

    /// <summary>
    /// Gets or sets the source URL of the tall (portrait) motion cover, or <see langword="null"/>
    /// when the service reported none. Stored in the album's <c>Backdrop</c> slot.
    /// </summary>
    public string? TallUrl { get; set; }

    /// <summary>
    /// Gets a value indicating whether the match carries at least one usable variant URL.
    /// </summary>
    public bool HasAny => !string.IsNullOrWhiteSpace(SquareUrl) || !string.IsNullOrWhiteSpace(TallUrl);
}

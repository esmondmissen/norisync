using System;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// One rendition listed in an HLS master playlist, resolved to an absolute media-playlist URL. The
/// declared resolution and codec are carried alongside it so the fetch can decide whether the
/// rendition can be copied as-is or has to be re-encoded, without opening it first.
/// </summary>
public sealed class HlsRendition
{
    /// <summary>
    /// Gets or sets the absolute URL of this rendition's media playlist.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the short edge, in pixels, of the rendition's declared <c>RESOLUTION</c>, or a
    /// negative value when it declares none. The short edge is what a quality step names, so it is
    /// also what rendition selection compares against.
    /// </summary>
    public int ShortEdge { get; set; }

    /// <summary>
    /// Gets or sets the rendition's declared <c>BANDWIDTH</c> in bits per second, or a negative
    /// value when it declares none.
    /// </summary>
    public long Bandwidth { get; set; }

    /// <summary>
    /// Gets or sets the rendition's declared <c>CODECS</c> string, e.g. <c>avc1.640020</c> or
    /// <c>hvc1.2.20000000.L123.B0</c>.
    /// </summary>
    public string? Codecs { get; set; }

    /// <summary>
    /// Gets a value indicating whether the video codec is HEVC. Copying HEVC into MP4 needs the
    /// <c>hvc1</c> sample-entry tag to stay playable on Apple platforms, so the fetch has to know.
    /// </summary>
    public bool IsHevc =>
        Codecs != null
        && (Codecs.Contains("hvc1", StringComparison.OrdinalIgnoreCase)
            || Codecs.Contains("hev1", StringComparison.OrdinalIgnoreCase));
}

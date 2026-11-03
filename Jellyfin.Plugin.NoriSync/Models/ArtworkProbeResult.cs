namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// The result of probing an uploaded animated-artwork file with ffprobe.
/// </summary>
public sealed class ArtworkProbeResult
{
    /// <summary>
    /// Gets or sets the ffprobe container format name(s), e.g. <c>"mov,mp4,m4a,3gp,3g2,mj2"</c>.
    /// </summary>
    public string FormatName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the video stream codec, e.g. <c>"h264"</c>, <c>"hevc"</c>, or <c>"av1"</c>.
    /// </summary>
    public string VideoCodec { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the video width in pixels, when reported.
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// Gets or sets the video height in pixels, when reported.
    /// </summary>
    public int? Height { get; set; }

    /// <summary>
    /// Gets or sets the duration in seconds, when reported.
    /// </summary>
    public double? DurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the overall bitrate in bits per second, when determinable. Derived from the
    /// file size and duration when a reliable duration is known (that is what a bandwidth cap
    /// actually meters), otherwise taken from ffprobe's reported value.
    /// </summary>
    public long? BitRateBps { get; set; }

    /// <summary>
    /// Gets or sets the video stream's pixel format, e.g. <c>"yuv420p"</c> or <c>"yuva420p"</c>.
    /// </summary>
    public string? PixelFormat { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the video carries an alpha channel, which a
    /// re-encode to H.264 <c>yuv420p</c> would flatten. Detected from the pixel format, or from the
    /// WebM <c>alpha_mode</c> stream tag — for VP9-alpha the plane lives in container metadata and
    /// ffprobe's native decoder still reports <c>yuv420p</c>.
    /// </summary>
    public bool HasAlpha { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the MP4 <c>moov</c> atom precedes <c>mdat</c>
    /// ("faststart"). <see langword="null"/> when undetermined.
    /// </summary>
    public bool? MoovAtFront { get; set; }
}

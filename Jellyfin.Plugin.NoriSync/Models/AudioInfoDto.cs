using MessagePack;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Exact, server-derived decode metadata for a single audio track. Lets the client do precise
/// end-of-file detection and gapless playback instead of approximating the playable length from
/// <c>RunTimeTicks</c> (a metadata-rounded duration that over-reports the real sample count).
/// </summary>
[MessagePackObject]
public class AudioInfoDto
{
    /// <summary>
    /// Gets or sets the audio codec name as reported by ffprobe (e.g. "flac", "mp3", "aac", "alac").
    /// </summary>
    [Key(0)]
    public string Codec { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sample rate in Hz.
    /// </summary>
    [Key(1)]
    public int SampleRate { get; set; }

    /// <summary>
    /// Gets or sets the channel count.
    /// </summary>
    [Key(2)]
    public int Channels { get; set; }

    /// <summary>
    /// Gets or sets the exact number of playable sample frames per channel (i.e. the decoded
    /// length after encoder delay/padding is trimmed). Use with <see cref="SampleRate"/> for
    /// exact EOF detection.
    /// </summary>
    [Key(3)]
    public long TotalFrames { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="TotalFrames"/> was derived exactly
    /// from container metadata (or a lossless stream's exact sample count), as opposed to being
    /// approximated from the reported duration. Clients should only treat <see cref="TotalFrames"/>
    /// as a hard EOF boundary when this is <see langword="true"/>.
    /// </summary>
    [Key(4)]
    public bool TotalFramesExact { get; set; }

    /// <summary>
    /// Gets or sets the encoder delay (priming samples) the encoder prepended, or <see langword="null"/>
    /// when not applicable (lossless) or unknown. Samples per channel.
    /// </summary>
    [Key(5)]
    public int? EncoderDelay { get; set; }

    /// <summary>
    /// Gets or sets the encoder padding samples appended to fill the final frame, or
    /// <see langword="null"/> when not applicable (lossless) or unknown. Samples per channel.
    /// </summary>
    [Key(6)]
    public int? EncoderPadding { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the MP4 <c>moov</c> atom precedes <c>mdat</c>
    /// ("faststart"). Only meaningful for MP4/M4A/MOV containers; <see langword="null"/> for
    /// every other container.
    /// </summary>
    [Key(7)]
    public bool? MoovAtFront { get; set; }

    /// <summary>
    /// Gets or sets the track's duration in 100-ns ticks as Jellyfin recorded it, echoed back so
    /// clients can cross-check against the exact <see cref="TotalFrames"/>.
    /// </summary>
    [Key(8)]
    public long DurationTicks { get; set; }
}

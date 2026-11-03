using System;
using System.Diagnostics.CodeAnalysis;
using MessagePack;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A single row in the waveform changes feed: the packed waveform blob for one track and the
/// analyzer version that produced it. Other acoustic features (LUFS, BPM, key) are intentionally
/// absent — LUFS is on Jellyfin's native <c>BaseItem.LUFS</c>; BPM and key were dropped in the
/// waveform-first redesign.
/// </summary>
[MessagePackObject]
public class WaveformChangeDto
{
    /// <summary>
    /// Gets or sets the Jellyfin item id.
    /// </summary>
    [Key(0)]
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp at which this record was written.
    /// </summary>
    [Key(1)]
    public DateTimeOffset ProcessedAt { get; set; }

    /// <summary>
    /// Gets or sets the source file's last-write timestamp at processing time, in ticks.
    /// </summary>
    [Key(2)]
    public long SourceMtime { get; set; }

    /// <summary>
    /// Gets or sets the packed low-resolution waveform bytes (5 bands × 30Hz, one byte per
    /// band per frame). Null when the analyzer hasn't produced a result for this track.
    /// </summary>
    [Key(3)]
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Waveform is an opaque packed byte blob; array semantics are intentional.")]
    public byte[]? Waveform { get; set; }

    /// <summary>
    /// Gets or sets the analyzer version that produced <see cref="Waveform"/>.
    /// </summary>
    [Key(4)]
    public string? WaveformVersion { get; set; }
}

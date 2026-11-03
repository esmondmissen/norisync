using System;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Persisted audio features for a single track: the waveform analysis bytes and the analyzer
/// version that produced them. Other acoustic features (LUFS, BPM, key) are intentionally
/// absent — LUFS lives on Jellyfin's native <c>BaseItem.LUFS</c> and the others were dropped
/// in favor of a leaner waveform-first contract.
/// </summary>
public class TrackFeature
{
    /// <summary>
    /// Gets or sets the Jellyfin item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the low-resolution waveform analysis bytes (5 bands × 30Hz). Null when
    /// the analyzer has not run for this track or the record predates waveform capture.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Waveform is an opaque packed byte blob; array semantics are intentional.")]
    public byte[]? Waveform { get; set; }

    /// <summary>
    /// Gets or sets the analyzer identifier that produced <see cref="Waveform"/>. Bumped when
    /// the analysis algorithm or output format materially changes so stale rows are re-processed.
    /// </summary>
    public string? WaveformVersion { get; set; }

    /// <summary>
    /// Gets or sets the source file's last-write timestamp at processing time.
    /// </summary>
    public long SourceMtime { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp the features were computed.
    /// </summary>
    public DateTimeOffset ProcessedAt { get; set; }
}

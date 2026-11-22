using System;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Persisted audio features for a single track: the waveform analysis bytes, the beat mix
/// descriptor, and the analyzer versions that produced them. Other acoustic features (LUFS, BPM, key) are intentionally
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
    /// Gets or sets the packed waveform bytes: a 4-byte header, then one 4-byte word per 30 Hz
    /// frame packing a kick flag, five onset flags and five 4-bit band magnitudes (API_FORMAT
    /// §4.10). Null when the analyzer has not run for this track or the record predates
    /// waveform capture.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Waveform is an opaque packed byte blob; array semantics are intentional.")]
    public byte[]? Waveform { get; set; }

    /// <summary>
    /// Gets or sets the analyzer identifier that produced <see cref="Waveform"/>. Bumped when
    /// the analysis algorithm or output format materially changes so stale rows are re-processed.
    /// </summary>
    public string? WaveformVersion { get; set; }

    /// <summary>
    /// Gets or sets the 48-byte beat mix descriptor (API_FORMAT §4.11). Null when the track was
    /// too short or silent for one, or the row predates the descriptor.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "The descriptor is an opaque packed byte blob; array semantics are intentional.")]
    public byte[]? MixProfile { get; set; }

    /// <summary>
    /// Gets or sets the <see cref="Services.MixProfileAnalyzer.Version"/> that produced
    /// <see cref="MixProfile"/> — set even when it produced none, so the track isn't re-analysed.
    /// Null when the descriptor hasn't been computed.
    /// </summary>
    public string? MixProfileVersion { get; set; }

    /// <summary>
    /// Gets or sets the source file's last-write timestamp at processing time.
    /// </summary>
    public long SourceMtime { get; set; }

    /// <summary>
    /// Gets or sets the source file's size in bytes at processing time. Null for a row from
    /// before sizes were stored.
    /// </summary>
    public long? SourceSize { get; set; }

    /// <summary>
    /// Gets or sets when something a client reads last changed: the feed's cursor. A re-analysis
    /// that changes nothing keeps it.
    /// </summary>
    public DateTimeOffset ProcessedAt { get; set; }

    /// <summary>
    /// Gets or sets when <see cref="Waveform"/> (or the libraries it's served under) last changed.
    /// A client whose cursor is past it already holds this waveform.
    /// </summary>
    public DateTimeOffset WaveformChangedAt { get; set; }
}

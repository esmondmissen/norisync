using System;
using System.Diagnostics.CodeAnalysis;
using MessagePack;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A single row in the waveform changes feed: the packed waveform blob and beat mix descriptor
/// for one track, and the analyzer versions that produced them. Other acoustic features (LUFS, BPM, key) are intentionally
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
    /// Gets or sets the packed waveform bytes: a 4-byte header, then one 4-byte word per 30 Hz
    /// frame (kick flag, five onset flags, five 4-bit band magnitudes; API_FORMAT §4.10). Null
    /// when the analyzer hasn't produced a result for this track.
    /// </summary>
    [Key(3)]
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Waveform is an opaque packed byte blob; array semantics are intentional.")]
    public byte[]? Waveform { get; set; }

    /// <summary>
    /// Gets or sets the analyzer version that produced <see cref="Waveform"/>.
    /// </summary>
    [Key(4)]
    public string? WaveformVersion { get; set; }

    /// <summary>
    /// Gets or sets the 48-byte <c>mixprofile-v1</c> beat descriptor (API_FORMAT §4.11). Null
    /// when the track has none (too short or silent, or not analysed yet).
    /// </summary>
    [Key(5)]
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "The descriptor is an opaque packed byte blob; array semantics are intentional.")]
    public byte[]? MixProfile { get; set; }

    /// <summary>
    /// Gets or sets the descriptor version that produced <see cref="MixProfile"/>
    /// (<c>"mixprofile-v1"</c>); set when the track was analysed even if no descriptor resulted.
    /// </summary>
    [Key(6)]
    public string? MixProfileVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="Waveform"/> was left out because the
    /// client already holds it: sent only to a client that asked (<c>omitUnchangedWaveforms</c>),
    /// for a row whose waveform hasn't changed since its cursor. The client keeps its stored
    /// waveform; <see cref="Waveform"/> null here never means "no waveform".
    /// </summary>
    [Key(7)]
    public bool WaveformUnchanged { get; set; }
}

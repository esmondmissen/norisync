namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// One region (head or tail) of a <see cref="MixProfile"/>: a steady beat lattice
/// <c>ReferenceBeat + k · Period</c>. Times are in 0.1 ms units (100 µs) from the first decoded
/// sample, the waveform's timeline; the period is in microseconds. Wire layout: API_FORMAT §4.11.
/// </summary>
public sealed record MixProfileRegion
{
    /// <summary>
    /// Gets the beat period in microseconds, folded into 80–160 BPM.
    /// </summary>
    public uint PeriodMicros { get; init; }

    /// <summary>
    /// Gets a lattice beat inside the region, near the middle of its supported beats (0.1 ms).
    /// </summary>
    public uint ReferenceBeat { get; init; }

    /// <summary>
    /// Gets the first confident lattice beat of the region (0.1 ms).
    /// </summary>
    public uint FirstBeat { get; init; }

    /// <summary>
    /// Gets the last confident lattice beat of the region (0.1 ms).
    /// </summary>
    public uint LastBeat { get; init; }

    /// <summary>
    /// Gets the lattice confidence, 0–255 (fraction of tracked beats on the lattice).
    /// </summary>
    public byte Confidence { get; init; }

    /// <summary>
    /// Gets the bar phase: beats <c>ReferenceBeat + (DownbeatOffset + 4m) · Period</c> are
    /// downbeats. 255 when unknown.
    /// </summary>
    public byte DownbeatOffset { get; init; } = 255;

    /// <summary>
    /// Gets the downbeat confidence, 0–255.
    /// </summary>
    public byte DownbeatConfidence { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="DownbeatOffset"/> is confident enough to use
    /// (the region's "downbeat confident" flag bit).
    /// </summary>
    public bool DownbeatConfident { get; init; }

    /// <summary>
    /// Gets the period in seconds.
    /// </summary>
    public double PeriodSeconds => PeriodMicros / 1_000_000.0;

    /// <summary>
    /// Gets the tempo in beats per minute.
    /// </summary>
    public double BeatsPerMinute => PeriodMicros == 0 ? 0 : 60_000_000.0 / PeriodMicros;

    /// <summary>
    /// Gets <see cref="ReferenceBeat"/> in seconds.
    /// </summary>
    public double ReferenceBeatSeconds => ReferenceBeat / 10_000.0;

    /// <summary>
    /// Gets <see cref="FirstBeat"/> in seconds.
    /// </summary>
    public double FirstBeatSeconds => FirstBeat / 10_000.0;

    /// <summary>
    /// Gets <see cref="LastBeat"/> in seconds.
    /// </summary>
    public double LastBeatSeconds => LastBeat / 10_000.0;
}

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A track's beat "mix descriptor" (<c>mixprofile-v1</c>): beat lattices fitted over the first
/// 60 s (<see cref="Head"/>) and the last 90 s (<see cref="Tail"/>), plus where the outro
/// recedes. Encoded as a fixed 48-byte blob by
/// <see cref="Services.MixProfileAnalyzer.Encode"/>; wire layout in API_FORMAT §4.11.
/// </summary>
public sealed record MixProfile
{
    /// <summary>
    /// Gets the lattice over the first 60 s, or null when that region has no clear beat.
    /// </summary>
    public MixProfileRegion? Head { get; init; }

    /// <summary>
    /// Gets the lattice over the last 90 s, or null when that region has no clear beat.
    /// </summary>
    public MixProfileRegion? Tail { get; init; }

    /// <summary>
    /// Gets the time (0.1 ms) after which low-band energy stays below about half the track's
    /// body level to the end, or null when the track ends at full energy.
    /// </summary>
    public uint? OutroRecedesAt { get; init; }

    /// <summary>
    /// Gets <see cref="OutroRecedesAt"/> in seconds.
    /// </summary>
    public double? OutroRecedesAtSeconds => OutroRecedesAt / 10_000.0;
}

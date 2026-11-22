namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Whether Lidarr follows an artist (<c>GET/PUT /api/norisync/lidarr/artists/{mbid}/monitoring</c>).
/// </summary>
public sealed class ArtistMonitoring
{
    /// <summary>
    /// Gets or sets a value indicating whether the artist is in Lidarr at all.
    /// </summary>
    public bool InLidarr { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Lidarr monitors the artist: RSS grabs releases
    /// of its monitored albums. False when <see cref="InLidarr"/> is false.
    /// </summary>
    public bool Monitored { get; set; }

    /// <summary>
    /// Gets or sets Lidarr's rule for albums it discovers later: <c>all</c>, <c>none</c> or
    /// <c>new</c> (only albums newer than the artist's latest). Null when not in Lidarr.
    /// </summary>
    public string? MonitorNewItems { get; set; }
}

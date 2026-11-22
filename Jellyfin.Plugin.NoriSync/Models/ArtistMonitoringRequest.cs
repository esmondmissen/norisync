namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Body of <c>PUT /api/norisync/lidarr/artists/{mbid}/monitoring</c>. A field left null is
/// left as it is.
/// </summary>
public sealed class ArtistMonitoringRequest
{
    /// <summary>
    /// Gets or sets whether Lidarr should monitor the artist. True for an artist not in Lidarr
    /// adds it, following only releases from now on.
    /// </summary>
    public bool? Monitored { get; set; }

    /// <summary>
    /// Gets or sets the rule for albums Lidarr discovers later: <c>all</c>, <c>none</c> or <c>new</c>.
    /// </summary>
    public string? MonitorNewItems { get; set; }
}

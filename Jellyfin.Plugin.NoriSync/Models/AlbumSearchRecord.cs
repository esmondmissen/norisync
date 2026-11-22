using System;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// The plugin's search schedule for one Lidarr album, shared by every user who requested it.
/// Removed with the album's request rows.
/// </summary>
public sealed class AlbumSearchRecord
{
    /// <summary>
    /// Gets or sets the Lidarr album id.
    /// </summary>
    public int LidarrAlbumId { get; set; }

    /// <summary>
    /// Gets or sets where the search stands.
    /// </summary>
    public AlbumSearchState State { get; set; }

    /// <summary>
    /// Gets or sets how many searches have been sent since <see cref="WindowStart"/>.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Gets or sets when the retry window began (the request, or the last "Search Again"). The
    /// schedule gives up <see cref="Services.AlbumSearchScheduler.RetryWindow"/> after it.
    /// </summary>
    public DateTimeOffset WindowStart { get; set; }

    /// <summary>
    /// Gets or sets when the last search was sent, or null before the first.
    /// </summary>
    public DateTimeOffset? LastSearchedAt { get; set; }

    /// <summary>
    /// Gets or sets when the next search is due; null when none is scheduled.
    /// </summary>
    public DateTimeOffset? NextSearchAt { get; set; }

    /// <summary>
    /// Gets or sets the Lidarr command id of the search in flight (<see cref="AlbumSearchState.Searching"/>).
    /// </summary>
    public int? CommandId { get; set; }

    /// <summary>
    /// Gets or sets Lidarr's last word on the search ("Album search completed. 0 reports downloaded.").
    /// </summary>
    public string? LastMessage { get; set; }
}

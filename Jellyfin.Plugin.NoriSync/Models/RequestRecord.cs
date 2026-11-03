using System;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A single tracked Lidarr request, attributing one Jellyfin user to one Lidarr album.
/// </summary>
public sealed class RequestRecord
{
    /// <summary>
    /// Gets or sets the Jellyfin user that submitted the request.
    /// </summary>
    public Guid JellyfinUserId { get; set; }

    /// <summary>
    /// Gets or sets the numeric Lidarr album id assigned after the album was added.
    /// </summary>
    public int LidarrAlbumId { get; set; }

    /// <summary>
    /// Gets or sets the MusicBrainz release-group id the request was made for.
    /// </summary>
    public string? AlbumMbid { get; set; }

    /// <summary>
    /// Gets or sets the album title, captured at request time for display.
    /// </summary>
    public string? AlbumTitle { get; set; }

    /// <summary>
    /// Gets or sets the artist name, captured at request time for display.
    /// </summary>
    public string? ArtistName { get; set; }

    /// <summary>
    /// Gets or sets when the request was submitted (UTC).
    /// </summary>
    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>
    /// Gets or sets the most recently observed status of the request.
    /// </summary>
    public RequestStatus Status { get; set; }

    /// <summary>
    /// Gets or sets when <see cref="Status"/> was last updated by the sync task.
    /// </summary>
    public DateTimeOffset StatusUpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the current download progress (0..1) when <see cref="Status"/> is
    /// <see cref="RequestStatus.Downloading"/>; null otherwise.
    /// </summary>
    public double? DownloadProgress { get; set; }

    /// <summary>
    /// Gets or sets the most recent error message from Lidarr's queue, when applicable.
    /// </summary>
    public string? LastError { get; set; }
}

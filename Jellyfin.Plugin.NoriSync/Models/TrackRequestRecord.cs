using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// One user's request for one song (a MusicBrainz recording), sent to SoulSync's wishlist.
/// </summary>
public sealed class TrackRequestRecord
{
    /// <summary>
    /// Gets or sets the Jellyfin user who asked.
    /// </summary>
    public Guid JellyfinUserId { get; set; }

    /// <summary>
    /// Gets or sets the MusicBrainz recording id asked for.
    /// </summary>
    public string RecordingMbid { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the key SoulSync stored the wishlist row under (what it answered the add
    /// with; it may differ from the id sent).
    /// </summary>
    public string? SoulSyncTrackId { get; set; }

    /// <summary>
    /// Gets or sets the song's title, captured when asked.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the artist name, captured when asked.
    /// </summary>
    public string? ArtistName { get; set; }

    /// <summary>
    /// Gets or sets the album the song was asked for from, when known.
    /// </summary>
    public string? AlbumTitle { get; set; }

    /// <summary>
    /// Gets or sets that album's MusicBrainz release-group id, when known (for its cover).
    /// </summary>
    public string? ReleaseGroupMbid { get; set; }

    /// <summary>
    /// Gets or sets when it was asked for (UTC).
    /// </summary>
    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>
    /// Gets or sets where it stands.
    /// </summary>
    public TrackRequestStatus Status { get; set; }

    /// <summary>
    /// Gets or sets when <see cref="Status"/> was last written.
    /// </summary>
    public DateTimeOffset StatusUpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the download progress (0..1) while <see cref="TrackRequestStatus.Downloading"/>.
    /// </summary>
    public double? DownloadProgress { get; set; }

    /// <summary>
    /// Gets or sets SoulSync's reason for the last failure, when it gave one.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets how many times SoulSync has tried it.
    /// </summary>
    public int? RetryCount { get; set; }

    /// <summary>
    /// Gets or sets when SoulSync last tried it.
    /// </summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>
    /// Gets or sets since when the song has been off SoulSync's wishlist without reaching the
    /// library; null while it is on the wishlist or downloading. Not sent to clients.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? WishlistMissingSince { get; set; }
}

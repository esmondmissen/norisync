using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Where a song request stands. Once the song is in the Jellyfin library the request is removed,
/// so there is no "available" state on the wire.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackRequestStatus
{
    /// <summary>
    /// On SoulSync's wishlist, waiting for its next run (it retries on its own, every half hour
    /// by default). <see cref="TrackRequestRecord.LastError"/> says why an earlier try failed.
    /// </summary>
    Queued,

    /// <summary>
    /// SoulSync is searching its sources for the song.
    /// </summary>
    Searching,

    /// <summary>
    /// The song is downloading (<see cref="TrackRequestRecord.DownloadProgress"/>).
    /// </summary>
    Downloading,

    /// <summary>
    /// Downloaded; being tagged and moved into the library, or waiting for Jellyfin's scan.
    /// </summary>
    Importing,

    /// <summary>
    /// The last try failed, or the song left SoulSync's wishlist without reaching the library.
    /// </summary>
    Failed,
}

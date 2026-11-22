using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Where the plugin's own search for a requested album stands. Lidarr's
/// <c>searchForNewAlbum</c> never runs for a new artist, a search before the release list
/// loads rejects every sized result, and Lidarr never re-searches a miss — so the plugin
/// searches itself (<see cref="Services.AlbumSearchScheduler"/>).
/// </summary>
/// <remarks>
/// On the wire beside <see cref="RequestStatus"/>, never as new values of it: older apps decode
/// <c>Status</c> strictly, and an unknown value fails their whole request list.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlbumSearchState
{
    /// <summary>
    /// Lidarr hasn't loaded the album's releases yet; searching now would reject every result.
    /// </summary>
    AwaitingMetadata,

    /// <summary>
    /// An <c>AlbumSearch</c> command is queued or running.
    /// </summary>
    Searching,

    /// <summary>
    /// The last search sent nothing to the download client. <c>NextSearchAt</c> says when the
    /// next one runs; null once the retry window has passed.
    /// </summary>
    NothingFound,

    /// <summary>
    /// The last search sent a release to the download client.
    /// </summary>
    Grabbed
}

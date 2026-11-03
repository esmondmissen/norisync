using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Lifecycle of a tracked Lidarr request. Once a request is fully imported it is removed
/// from the store (Lidarr becomes the source of truth), so there is no <c>Imported</c>
/// state on the wire.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequestStatus
{
    /// <summary>
    /// Album has been submitted to Lidarr but no download is in flight yet (searching,
    /// queued for indexer, waiting for cron, etc.).
    /// </summary>
    Pending,

    /// <summary>
    /// A release has been grabbed and is being downloaded.
    /// </summary>
    Downloading,

    /// <summary>
    /// Download has completed and Lidarr is importing files into the library.
    /// </summary>
    Importing,

    /// <summary>
    /// Lidarr or the download client surfaced an error. <see cref="RequestRecord.LastError"/>
    /// carries the message.
    /// </summary>
    Failed,

    /// <summary>
    /// Download finished but Lidarr can't progress without human action — typically a
    /// completed download sitting in the activity queue with a warning (unmapped tracks,
    /// missing audio files, manual import required, etc.). <see cref="RequestRecord.LastError"/>
    /// carries the reason surfaced by Lidarr's <c>statusMessages</c>.
    /// </summary>
    Stalled
}

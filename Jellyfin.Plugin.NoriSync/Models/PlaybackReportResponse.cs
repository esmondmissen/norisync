namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Response of <c>POST /api/norisync/playback/report</c> when the device has something to do.
/// </summary>
public sealed class PlaybackReportResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the report was older than one already applied, and
    /// was dropped.
    /// </summary>
    public bool Stale { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the server lacks the queue the report names: send it.
    /// </summary>
    public bool QueueNeeded { get; set; }

    /// <summary>
    /// Gets or sets where the device's session went, when another device took it over and this
    /// device still plays it: pause.
    /// </summary>
    public PlaybackMovedTo? MovedTo { get; set; }
}

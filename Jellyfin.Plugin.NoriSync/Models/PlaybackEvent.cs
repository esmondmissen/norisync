namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Which report a device sent (API_FORMAT §3.20).
/// </summary>
public enum PlaybackEvent
{
    /// <summary>
    /// A track started: Jellyfin's <c>Sessions/Playing</c>.
    /// </summary>
    Start,

    /// <summary>
    /// A pause, resume, seek or heartbeat: Jellyfin's <c>Sessions/Playing/Progress</c>.
    /// </summary>
    Progress,

    /// <summary>
    /// A track stopped: Jellyfin's <c>Sessions/Playing/Stopped</c>.
    /// </summary>
    Stopped,

    /// <summary>
    /// The device moved, or its queue changed, with no Jellyfin event (the queue's end wrapping to
    /// its first track, Next while paused, a queue edit). Never passed on to Jellyfin.
    /// </summary>
    Cursor,
}

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// What a device is doing with its session, as its latest report says.
/// </summary>
public enum PlaybackStatus
{
    /// <summary>
    /// Nothing loaded, or playback was stopped.
    /// </summary>
    Stopped = 0,

    /// <summary>
    /// A track is loaded and paused.
    /// </summary>
    Paused = 1,

    /// <summary>
    /// A track is playing.
    /// </summary>
    Playing = 2,
}

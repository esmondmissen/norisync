namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Where a device's session went: the device that took it over.
/// </summary>
public sealed class PlaybackMovedTo
{
    /// <summary>
    /// Gets or sets the device that took the session.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets that device's name, if it gave one.
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Gets or sets the session that moved: a device still playing it pauses, one playing another
    /// session doesn't.
    /// </summary>
    public string? SessionId { get; set; }
}

using System.Collections.Generic;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Response of <c>GET /api/norisync/playback/devices</c>.
/// </summary>
public sealed class PlaybackDevicesResponse
{
    /// <summary>
    /// Gets or sets the server's clock when it answered, Unix milliseconds.
    /// </summary>
    public long ServerNow { get; set; }

    /// <summary>
    /// Gets or sets the highest version among the devices.
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    /// Gets or sets every device of the user.
    /// </summary>
    public IReadOnlyList<PlaybackDeviceDto> Devices { get; set; } = [];
}

using System;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// A device's first socket opened, or its last one closed (<see cref="DeviceSocketRegistry.SocketsChanged"/>).
/// </summary>
public sealed class DeviceSocketsChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceSocketsChangedEventArgs"/> class.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device.</param>
    public DeviceSocketsChangedEventArgs(Guid userId, string deviceId)
    {
        UserId = userId;
        DeviceId = deviceId;
    }

    /// <summary>
    /// Gets the user.
    /// </summary>
    public Guid UserId { get; }

    /// <summary>
    /// Gets the device.
    /// </summary>
    public string DeviceId { get; }
}

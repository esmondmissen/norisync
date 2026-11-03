using System;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A registered client instance, identified by the pairing of a Jellyfin user and a device.
/// Used to gate change-log cleanup so we never purge entries a client still needs to sync.
/// </summary>
public class AppInstance
{
    /// <summary>
    /// Gets or sets the Jellyfin user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin device id (stable per client install).
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human readable device name reported by the client, if any.
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Gets or sets the client name reported by the client (app / brand), if any.
    /// </summary>
    public string? ClientName { get; set; }

    /// <summary>
    /// Gets or sets the time the instance first contacted the plugin.
    /// </summary>
    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>
    /// Gets or sets the most recent time the instance contacted the plugin.
    /// </summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// Gets or sets the server timestamp the client has confirmed it has fully reconciled up to,
    /// or <see langword="null"/> if the client has never acknowledged a sync.
    /// </summary>
    public DateTimeOffset? LastFullSyncAck { get; set; }
}

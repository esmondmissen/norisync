using System.Globalization;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A device as clients see it, in <c>GET playback/devices</c> and in pushes (API_FORMAT §3.20).
/// Times are the server's clock, Unix milliseconds.
/// </summary>
public sealed class PlaybackDeviceDto
{
    /// <summary>
    /// Gets or sets the device.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what the device calls itself, if anything.
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is the device asking.
    /// </summary>
    public bool IsCaller { get; set; }

    /// <summary>
    /// Gets or sets the session it holds, if any.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Gets or sets that session's queue revision.
    /// </summary>
    public int? QueueRev { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether other devices may be offered the session.
    /// </summary>
    public bool Shareable { get; set; }

    /// <summary>
    /// Gets or sets the current entry in the queue.
    /// </summary>
    public int EntryIndex { get; set; }

    /// <summary>
    /// Gets or sets the current item, 32 lowercase hex.
    /// </summary>
    public string? ItemId { get; set; }

    /// <summary>
    /// Gets or sets the position at <see cref="PositionAt"/>, in ticks.
    /// </summary>
    public long PositionTicks { get; set; }

    /// <summary>
    /// Gets or sets when the position was true.
    /// </summary>
    public long PositionAt { get; set; }

    /// <summary>
    /// Gets or sets <c>playing</c>, <c>paused</c> or <c>stopped</c>.
    /// </summary>
    public string Status { get; set; } = "stopped";

    /// <summary>
    /// Gets or sets a value indicating whether the device is playing now and can be told to pause.
    /// </summary>
    public bool Live { get; set; }

    /// <summary>
    /// Gets or sets when the device last reported.
    /// </summary>
    public long UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the record's version: a client keeps the higher.
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    /// Gets or sets where the device's session went, when another device took it over.
    /// </summary>
    public PlaybackMovedTo? MovedTo { get; set; }

    /// <summary>
    /// The wire form of a stored device.
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="isCaller">Whether it is the device being answered.</param>
    /// <param name="live">Whether it is live now.</param>
    /// <returns>The DTO.</returns>
    public static PlaybackDeviceDto From(PlaybackDevice device, bool isCaller, bool live) => new()
    {
        DeviceId = device.DeviceId,
        DeviceName = device.DeviceName,
        IsCaller = isCaller,
        SessionId = device.SessionId,
        QueueRev = device.QueueRev,
        Shareable = device.Shareable,
        EntryIndex = device.EntryIndex,
        ItemId = device.ItemId,
        PositionTicks = device.PositionTicks,
        PositionAt = device.PositionAt,
        Status = device.Status.ToString().ToLower(CultureInfo.InvariantCulture),
        Live = live,
        UpdatedAt = device.UpdatedAt,
        Version = device.Version,
        MovedTo = device.MovedTo == null
            ? null
            : new PlaybackMovedTo { DeviceId = device.MovedTo, DeviceName = device.MovedToName, SessionId = device.MovedSessionId }
    };
}

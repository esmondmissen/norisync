using System;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// One of a user's devices as the server holds it: its session and where it is in it. Times are
/// the server's clock, Unix milliseconds.
/// </summary>
/// <param name="UserId">The user.</param>
/// <param name="DeviceId">The device.</param>
/// <param name="DeviceName">What the device calls itself, if anything.</param>
/// <param name="SessionId">The session it holds; <see langword="null"/> when it holds none (stopped, or moved away).</param>
/// <param name="QueueRev">That session's queue revision.</param>
/// <param name="Shareable">Whether other devices may be offered the session.</param>
/// <param name="EntryIndex">The current entry in the queue.</param>
/// <param name="ItemId">The current item, 32 lowercase hex.</param>
/// <param name="PositionTicks">The position at <paramref name="PositionAt"/>.</param>
/// <param name="PositionAt">When the position was true.</param>
/// <param name="Status">What the device is doing.</param>
/// <param name="LastSeq">The last report number applied.</param>
/// <param name="Version">Moves on at every change of this record.</param>
/// <param name="UpdatedAt">When the device last reported.</param>
/// <param name="MovedTo">The device its session moved to by a take-over, if any.</param>
/// <param name="MovedToName">That device's name.</param>
/// <param name="MovedSessionId">The session that moved.</param>
public sealed record PlaybackDevice(
    Guid UserId,
    string DeviceId,
    string? DeviceName,
    string? SessionId,
    int? QueueRev,
    bool Shareable,
    int EntryIndex,
    string? ItemId,
    long PositionTicks,
    long PositionAt,
    PlaybackStatus Status,
    long LastSeq,
    long Version,
    long UpdatedAt,
    string? MovedTo,
    string? MovedToName,
    string? MovedSessionId);

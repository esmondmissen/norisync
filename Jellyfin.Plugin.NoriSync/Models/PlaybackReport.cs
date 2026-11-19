using System;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A validated <see cref="PlaybackReportRequest"/>.
/// </summary>
/// <param name="Seq">The device's report number: a report at or below the last applied is stale.</param>
/// <param name="Event">Which report.</param>
/// <param name="SessionId">The session (a queue lineage) the device is on.</param>
/// <param name="QueueRev">The revision of that session's queue.</param>
/// <param name="Shareable">Whether other devices may be offered the session.</param>
/// <param name="Queue">The queue for <paramref name="SessionId"/> at <paramref name="QueueRev"/>, a JSON object, when sent.</param>
/// <param name="EntryIndex">The current entry in the queue.</param>
/// <param name="ItemId">The item playing.</param>
/// <param name="PositionTicks">The position, in ticks (10,000 per millisecond).</param>
/// <param name="Status">What the device is doing after this report.</param>
/// <param name="PlaySessionId">The play session (binds the report to a transcode), if any.</param>
/// <param name="PlayMethod">How the item is played.</param>
/// <param name="TakeOverSessionId">The session this device is taking from whoever holds it, if any.</param>
public sealed record PlaybackReport(
    long Seq,
    PlaybackEvent Event,
    string SessionId,
    int QueueRev,
    bool Shareable,
    string? Queue,
    int EntryIndex,
    Guid ItemId,
    long PositionTicks,
    PlaybackStatus Status,
    string? PlaySessionId,
    PlayMethod PlayMethod,
    string? TakeOverSessionId);

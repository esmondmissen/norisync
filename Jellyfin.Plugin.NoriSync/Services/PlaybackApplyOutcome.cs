using System.Collections.Generic;
using Jellyfin.Plugin.NoriSync.Models;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// What applying one report did (<see cref="PlaybackStore.ApplyReport"/>).
/// </summary>
/// <param name="Stale">The report was older than one already applied: nothing changed.</param>
/// <param name="QueueNeeded">The server holds no queue for the session and revision the report names.</param>
/// <param name="Moved">The reporting device's session was taken over by another device; its record holds no session.</param>
/// <param name="TookOverFrom">The device whose session the report took over, if any: it is to be paused.</param>
/// <param name="Changed">Every record the report changed: the reporter's, and a taken-over device's.</param>
public sealed record PlaybackApplyOutcome(
    bool Stale,
    bool QueueNeeded,
    PlaybackDevice? Moved,
    string? TookOverFrom,
    IReadOnlyList<PlaybackDevice> Changed)
{
    /// <summary>
    /// Gets the outcome of a stale report.
    /// </summary>
    public static PlaybackApplyOutcome StaleReport { get; } = new(true, false, null, null, []);
}

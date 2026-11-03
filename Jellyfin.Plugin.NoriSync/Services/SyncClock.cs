using System;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Issues the timestamps the sync feeds are paged by, so a cursor can never skip a row.
/// </summary>
/// <remarks>
/// <para>
/// Clients page a feed with <c>until</c> taken from <c>/timestamp</c> and resume from it as an
/// exclusive <c>since</c>. That only works if no row can be stamped at or before a cursor and
/// become visible after the cursor is handed out. Stamping a row with the wall clock and saving
/// it later broke this: a change buffered for the flush window was stamped when its event fired
/// but saved up to seconds later, so a client that took a cursor in between skipped it for good.
/// </para>
/// <para>
/// Here a stamp is taken and its row committed under one lock, and cursors are issued under the
/// same lock, so a cursor never falls between a stamp and its commit. Stamps are also strictly
/// increasing — never equal to an earlier stamp or cursor — so the latest timestamp a page
/// returned is as safe a cursor as one from <c>/timestamp</c>. Both feeds share the one clock
/// because they share <c>/timestamp</c>.
/// </para>
/// </remarks>
public static class SyncClock
{
    private static readonly object Lock = new();
    private static long _lastMs;

    /// <summary>
    /// Runs <paramref name="write"/> with a stamp later than every stamp and cursor issued before
    /// it. No cursor is issued until <paramref name="write"/> returns, so it must commit the rows
    /// it stamps before returning.
    /// </summary>
    /// <param name="write">Writes and commits the rows, given their stamp.</param>
    public static void Stamp(Action<DateTimeOffset> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        lock (Lock)
        {
            _lastMs = Math.Max(NowMs(), _lastMs + 1);
            write(DateTimeOffset.FromUnixTimeMilliseconds(_lastMs));
        }
    }

    /// <summary>
    /// Issues a cursor: every row stamped at or before it is already committed, and every row
    /// stamped from now on is stamped after it.
    /// </summary>
    /// <returns>The cursor.</returns>
    public static DateTimeOffset IssueCursor()
    {
        lock (Lock)
        {
            _lastMs = Math.Max(NowMs(), _lastMs);
            return DateTimeOffset.FromUnixTimeMilliseconds(_lastMs);
        }
    }

    /// <summary>
    /// Clamps a client-supplied <c>until</c> to a cursor issued now, so a value from the future
    /// can't reach past rows that are yet to be committed.
    /// </summary>
    /// <param name="until">The requested upper bound.</param>
    /// <returns>The bound to query with.</returns>
    public static DateTimeOffset ClampUntil(DateTimeOffset until)
    {
        var cursor = IssueCursor();
        return until > cursor ? cursor : until;
    }

    /// <summary>
    /// Advances the clock past a stamp persisted by an earlier run, so stamps keep increasing
    /// across a restart even if the wall clock reads earlier.
    /// </summary>
    /// <param name="stamp">The latest persisted stamp.</param>
    public static void Observe(DateTimeOffset stamp)
    {
        lock (Lock)
        {
            _lastMs = Math.Max(_lastMs, stamp.ToUnixTimeMilliseconds());
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

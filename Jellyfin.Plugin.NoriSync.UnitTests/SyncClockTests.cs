using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Tests for <see cref="SyncClock"/> and the change feed stamped by it. The guarantee clients
/// rely on: resuming from a cursor (from <c>/timestamp</c> or a page's latest timestamp) as an
/// exclusive <c>since</c> never skips a change, however the cursor and the write interleave.
/// </summary>
public sealed class SyncClockTests : IDisposable
{
    private readonly string _tempDir;

    public SyncClockTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "norisync-unittests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Leave the temp dir on Windows file-lock contention; nothing important leaks.
        }
    }

    [Fact]
    public void StampsAreStrictlyIncreasingAndAfterEveryIssuedCursor()
    {
        var previous = SyncClock.IssueCursor();
        for (var i = 0; i < 1000; i++)
        {
            DateTimeOffset stamp = default;
            SyncClock.Stamp(s => stamp = s);
            Assert.True(stamp > previous);

            var cursor = SyncClock.IssueCursor();
            Assert.True(cursor >= stamp);
            previous = cursor;
        }
    }

    [Fact]
    public void ObserveKeepsStampsAheadOfAPersistedStamp()
    {
        var future = DateTimeOffset.UtcNow.AddHours(1);
        SyncClock.Observe(future);

        DateTimeOffset stamp = default;
        SyncClock.Stamp(s => stamp = s);

        Assert.True(stamp > future);
    }

    [Fact]
    public void ClampUntilCapsAFutureValueAtACursor()
    {
        var far = DateTimeOffset.UtcNow.AddDays(1);
        var clamped = SyncClock.ClampUntil(far);

        Assert.True(clamped < far);
        Assert.Equal(DateTimeOffset.UnixEpoch, SyncClock.ClampUntil(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void AChangeBufferedBeforeACursorIsSeenAfterIt()
    {
        // The bug this fixes: an event stamped when it fired, saved after the flush window, landed
        // behind a cursor the client had already been handed.
        using var store = new ChangeTrackingStore(_tempDir, NullLogger.Instance);
        var buffered = MakeRecord();
        buffered.Timestamp = DateTimeOffset.UtcNow.AddSeconds(-5);

        var cursor = SyncClock.IssueCursor();
        store.AddChange(buffered);

        var seen = store.GetChanges(cursor, until: null, itemTypes: null).Changes;
        Assert.Contains(seen, c => c.ItemId == buffered.ItemId);
    }

    [Fact]
    public void PagingFromCursorsWhileWritersRunSeesEveryChangeExactlyOnce()
    {
        using var store = new ChangeTrackingStore(_tempDir, NullLogger.Instance);
        const int Writers = 4;
        const int BatchesPerWriter = 50;
        var written = new List<Guid>();
        var seen = new List<Guid>();
        var done = 0;

        var writers = Enumerable.Range(0, Writers).Select(_ => Task.Run(() =>
        {
            for (var b = 0; b < BatchesPerWriter; b++)
            {
                // Stamped in the past, as a buffered event is.
                var batch = Enumerable.Range(0, 3).Select(_ =>
                {
                    var r = MakeRecord();
                    r.Timestamp = DateTimeOffset.UtcNow.AddSeconds(-5);
                    return r;
                }).ToList();
                store.AddChanges(batch);
                lock (written)
                {
                    written.AddRange(batch.Select(r => r.ItemId));
                }
            }

            Interlocked.Increment(ref done);
        })).ToArray();

        var reader = Task.Run(() =>
        {
            DateTimeOffset? since = null;
            while (true)
            {
                var finished = Volatile.Read(ref done) == Writers;
                var until = SyncClock.IssueCursor();
                seen.AddRange(store.GetChanges(since, until, itemTypes: null).Changes.Select(c => c.ItemId));
                since = until;
                if (finished)
                {
                    return;
                }
            }
        });

        Task.WaitAll(writers.Append(reader).ToArray());

        Assert.Equal(Writers * BatchesPerWriter * 3, written.Count);
        Assert.Equal(written.OrderBy(id => id), seen.OrderBy(id => id));
    }

    private static ChangeRecord MakeRecord() => new()
    {
        ItemId = Guid.NewGuid(),
        ItemName = "Track",
        ItemType = TrackedItemType.Track,
        ChangeType = ChangeType.Modified
    };
}

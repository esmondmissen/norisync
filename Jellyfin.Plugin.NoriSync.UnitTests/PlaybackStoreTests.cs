using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Each device's record and the queues they name (API_FORMAT §3.20).
/// </summary>
public sealed class PlaybackStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid User = Guid.NewGuid();

    private readonly string _tempDir;
    private readonly PlaybackStore _store;

    public PlaybackStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "norisync-playback-tests-" + Guid.NewGuid().ToString("N"));
        _store = new PlaybackStore(_tempDir, NullLogger.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private PlaybackApplyOutcome Apply(string device, PlaybackReport report, double seconds = 0) =>
        _store.ApplyReport(User, device, device + " name", report, Now.AddSeconds(seconds));

    [Fact]
    public void AReportIsTheDeviceAsItIsOnTheServersClock()
    {
        var outcome = Apply("mac", Reports.Make(1, entry: 3, ticks: 50));

        var mac = Assert.Single(_store.GetDevices(User));
        Assert.Equal(new PlaybackDevice(User, "mac", "mac name", "s1", 1, true, 3, Reports.Item.ToString("N"), 50,
            Now.ToUnixTimeMilliseconds(), PlaybackStatus.Playing, 1, mac.Version, Now.ToUnixTimeMilliseconds(), null, null, null), mac);
        Assert.Equal(mac, Assert.Single(outcome.Changed));
        Assert.False(outcome.QueueNeeded);
        Assert.Equal("{\"trackIds\":[\"a\"]}", _store.GetQueue(User, "s1", 1));
    }

    [Fact]
    public void AReportNumberedAtOrBelowTheLastIsStale()
    {
        Apply("mac", Reports.Make(10, entry: 1));
        Assert.True(Apply("mac", Reports.Make(10, entry: 2)).Stale);
        Assert.True(Apply("mac", Reports.Make(9, entry: 2)).Stale);
        Assert.Equal(1, Assert.Single(_store.GetDevices(User)).EntryIndex);

        Assert.False(Apply("mac", Reports.Make(11, entry: 2)).Stale);
        Assert.Equal(2, Assert.Single(_store.GetDevices(User)).EntryIndex);
    }

    /// <summary>
    /// A device whose numbers start again (clock moved back, reinstalled) would be stale for ever.
    /// </summary>
    [Fact]
    public void ANumberFarBelowTheLastIsADeviceThatStartedAgain()
    {
        var day = (long)PlaybackStore.SeqResetWindow.TotalMilliseconds;
        Apply("mac", Reports.Make(10 * day));
        Assert.False(Apply("mac", Reports.Make(10 * day - day - 1)).Stale);
        Assert.True(PlaybackStore.IsStale(10 * day - day, 10 * day));
    }

    [Fact]
    public void AQueueIsImmutableUnderItsRevisionAndNeededWhenMissing()
    {
        Apply("mac", Reports.Make(1, queue: "{\"v\":1}"));
        Apply("mac", Reports.Make(2, queue: "{\"v\":2}"));
        Assert.Equal("{\"v\":1}", _store.GetQueue(User, "s1", 1));

        var outcome = Apply("mac", Reports.Make(3, rev: 2, queue: null));
        Assert.True(outcome.QueueNeeded);
        Assert.False(Apply("mac", Reports.Make(4, rev: 2, queue: "{\"v\":3}", kind: PlaybackEvent.Cursor)).QueueNeeded);
    }

    [Fact]
    public void AnUnshareableSessionStoresAndNeedsNoQueue()
    {
        var outcome = Apply("mac", Reports.Make(1, session: "dj", shareable: false));
        Assert.False(outcome.QueueNeeded);
        Assert.Null(_store.GetQueue(User, "dj", 1));
        Assert.False(Assert.Single(_store.GetDevices(User)).Shareable);
    }

    [Fact]
    public void OnlyQueuesARecordNamesAreKept()
    {
        Apply("mac", Reports.Make(1, rev: 1));
        Apply("mac", Reports.Make(2, rev: 2, queue: "{\"v\":2}"));
        Assert.Null(_store.GetQueue(User, "s1", 1));
        Assert.NotNull(_store.GetQueue(User, "s1", 2));
    }

    [Fact]
    public void ATakeOverMovesTheSessionAndMarksTheSource()
    {
        Apply("mac", Reports.Make(1, session: "x"));
        var outcome = Apply("phone", Reports.Make(1, session: "x", queue: null, takeOver: "x"), 5);

        Assert.Equal("mac", outcome.TookOverFrom);
        Assert.False(outcome.QueueNeeded);
        var mac = _store.GetDevice(User, "mac")!;
        Assert.Null(mac.SessionId);
        Assert.Equal(PlaybackStatus.Paused, mac.Status);
        Assert.Equal(("phone", "phone name", "x"), (mac.MovedTo, mac.MovedToName, mac.MovedSessionId));
        Assert.Equal("x", _store.GetDevice(User, "phone")!.SessionId);
        Assert.Equal(["phone", "mac"], outcome.Changed.Select(d => d.DeviceId));
        Assert.NotNull(_store.GetQueue(User, "x", 1));
    }

    /// <summary>
    /// Two devices tapping at once: the later start takes it from whoever holds it then.
    /// </summary>
    [Fact]
    public void OfTwoTakeOversTheLaterWins()
    {
        Apply("mac", Reports.Make(1, session: "x"));
        Apply("phone", Reports.Make(1, session: "x", queue: null, takeOver: "x"), 1);
        var second = Apply("ipad", Reports.Make(1, session: "x", queue: null, takeOver: "x"), 2);

        Assert.Equal("phone", second.TookOverFrom);
        Assert.Equal("ipad", _store.GetDevice(User, "phone")!.MovedTo);
        Assert.Equal(["ipad"], _store.GetDevices(User).Where(d => d.SessionId == "x").Select(d => d.DeviceId));
    }

    [Fact]
    public void ATakeOverOfASessionNobodyHoldsMovesNothing()
    {
        var outcome = Apply("phone", Reports.Make(1, session: "x", takeOver: "x"));
        Assert.Null(outcome.TookOverFrom);
        Assert.Equal("x", _store.GetDevice(User, "phone")!.SessionId);
    }

    /// <summary>
    /// A source the pause didn't reach reports the session it lost: it gets no session back, its
    /// record says what it is doing, and the answer tells it where the session went.
    /// </summary>
    [Fact]
    public void AMovedDeviceReportingItsOldSessionIsToldAndHoldsNothing()
    {
        Apply("mac", Reports.Make(1, session: "x"));
        Apply("phone", Reports.Make(1, session: "x", queue: null, takeOver: "x"), 1);

        var outcome = Apply("mac", Reports.Make(2, session: "x", queue: null, kind: PlaybackEvent.Progress, ticks: 99), 2);
        Assert.NotNull(outcome.Moved);
        Assert.False(outcome.QueueNeeded);
        var mac = _store.GetDevice(User, "mac")!;
        Assert.Null(mac.SessionId);
        Assert.Equal((PlaybackStatus.Playing, 99L, "phone"), (mac.Status, mac.PositionTicks, mac.MovedTo));
        Assert.Equal("x", _store.GetDevice(User, "phone")!.SessionId);
    }

    /// <summary>
    /// The new holder uploads its own copy as the next revision; the source must not plant its
    /// queue there first.
    /// </summary>
    [Fact]
    public void AMovedDevicesQueueIsNotStored()
    {
        Apply("mac", Reports.Make(1, session: "x"));
        Apply("phone", Reports.Make(1, session: "x", rev: 2, queue: "{\"by\":\"phone\"}", takeOver: "x"), 1);
        Apply("mac", Reports.Make(2, session: "x", rev: 3, queue: "{\"by\":\"mac\"}"), 2);
        Assert.Null(_store.GetQueue(User, "x", 3));
        Assert.Equal("{\"by\":\"phone\"}", _store.GetQueue(User, "x", 2));
    }

    /// <summary>
    /// Mac → iPhone → Mac: the session goes back, and the device it moved from the second time is
    /// marked, not the one taking it back.
    /// </summary>
    [Fact]
    public void ADeviceTakesBackTheSessionItLost()
    {
        Apply("mac", Reports.Make(1, session: "x"));
        Apply("phone", Reports.Make(1, session: "x", rev: 2, takeOver: "x"), 1);

        var back = Apply("mac", Reports.Make(2, session: "x", rev: 3, takeOver: "x"), 2);

        Assert.Equal("phone", back.TookOverFrom);
        Assert.Null(back.Moved);
        var mac = _store.GetDevice(User, "mac")!;
        Assert.Equal(("x", (string?)null), (mac.SessionId, mac.MovedTo));
        var phone = _store.GetDevice(User, "phone")!;
        Assert.Equal(((string?)null, "mac", "x"), (phone.SessionId, phone.MovedTo, phone.MovedSessionId));
        Assert.NotNull(_store.GetQueue(User, "x", 3));
    }

    [Fact]
    public void AMovedDeviceStartingAnotherSessionIsNoLongerMoved()
    {
        Apply("mac", Reports.Make(1, session: "x"));
        Apply("phone", Reports.Make(1, session: "x", queue: null, takeOver: "x"), 1);

        var outcome = Apply("mac", Reports.Make(2, session: "y"), 2);
        Assert.Null(outcome.Moved);
        var mac = _store.GetDevice(User, "mac")!;
        Assert.Equal(("y", (string?)null, (string?)null), (mac.SessionId, mac.MovedTo, mac.MovedSessionId));
    }

    [Fact]
    public void AStopClearsOnlyTheSessionTheDeviceStillHolds()
    {
        Apply("mac", Reports.Make(1, session: "x"));
        Assert.Null(_store.ClearSession(User, "mac", "old", Now));

        var cleared = _store.ClearSession(User, "mac", "x", Now.AddSeconds(1))!;
        Assert.Equal((null, PlaybackStatus.Stopped), (cleared.SessionId, cleared.Status));
        Assert.Null(_store.GetQueue(User, "x", 1));
    }

    [Fact]
    public void VersionsOnlyMoveOnEvenWhenTheClockGoesBack()
    {
        Apply("mac", Reports.Make(1));
        var first = _store.GetDevice(User, "mac")!.Version;
        Apply("mac", Reports.Make(2), -3600);
        Assert.True(_store.GetDevice(User, "mac")!.Version > first);
        Assert.True(first >= Now.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void DeviceIdsAreCaseInsensitiveAsJellyfinsAre()
    {
        Apply("Mac-Device", Reports.Make(1));
        Apply("mac-device", Reports.Make(2, entry: 5));
        var device = Assert.Single(_store.GetDevices(User));
        Assert.Equal(("Mac-Device", 5), (device.DeviceId, device.EntryIndex));
    }

    [Fact]
    public void TheLeastRecentlyHeardDevicesGoPastTheCapAndAfterAMonth()
    {
        for (var i = 0; i < PlaybackStore.MaxDevicesPerUser + 2; i++)
        {
            Apply("d" + i, Reports.Make(1, session: "s" + i), i);
        }

        var devices = _store.GetDevices(User).Select(d => d.DeviceId).ToHashSet();
        Assert.Equal(PlaybackStore.MaxDevicesPerUser, devices.Count);
        Assert.DoesNotContain("d0", devices);
        Assert.DoesNotContain("d1", devices);

        Apply("late", Reports.Make(1, session: "late"), PlaybackStore.StaleAfter.TotalSeconds + 100);
        Assert.Equal(["late"], _store.GetDevices(User).Select(d => d.DeviceId));
    }

    [Fact]
    public void UsersAreKeptApart()
    {
        Apply("mac", Reports.Make(1, session: "x"));
        var other = Guid.NewGuid();
        var outcome = _store.ApplyReport(other, "phone", null, Reports.Make(1, session: "x", queue: null, takeOver: "x"), Now);

        Assert.Null(outcome.TookOverFrom);
        Assert.True(outcome.QueueNeeded);
        Assert.Equal("x", _store.GetDevice(User, "mac")!.SessionId);
        Assert.Null(_store.GetQueue(other, "x", 1));
    }

    [Fact]
    public void PlayingDevicesAreFoundForTheLivenessWatch()
    {
        Apply("mac", Reports.Make(1));
        Apply("phone", Reports.Make(1, status: PlaybackStatus.Paused, kind: PlaybackEvent.Progress));
        Assert.Equal(["mac"], _store.GetPlayingDevices(Now.AddMinutes(-5)).Select(d => d.DeviceId));
        Assert.Empty(_store.GetPlayingDevices(Now.AddSeconds(1)));
    }

    [Fact]
    public void ADeletedUserLeavesNothing()
    {
        Apply("mac", Reports.Make(1));
        Assert.Contains(User, _store.GetAllUserIds());
        _store.DeleteUser(User);
        Assert.Empty(_store.GetDevices(User));
        Assert.Null(_store.GetQueue(User, "s1", 1));
        Assert.DoesNotContain(User, _store.GetAllUserIds());
    }
}

using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.NoriSync.Api;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Feed-semantics tests for <see cref="ListenHistoryStore"/>: a listen is stored once however
/// often it is uploaded, a device never reads its own listens back, the cursor steps over them
/// anyway, users never see each other's listens, and the epoch survives a restart.
/// </summary>
public sealed class ListenHistoryStoreTests : IDisposable
{
    private const string Phone = "phone";
    private const string Mac = "mac";

    private readonly string _tempDir;
    private ListenHistoryStore _store;

    public ListenHistoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "norisync-listens-tests-" + Guid.NewGuid().ToString("N"));
        _store = new ListenHistoryStore(_tempDir, NullLogger.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Leave the temp dir on file-lock contention; nothing important leaks.
        }
    }

    [Fact]
    public void Add_SameListenTwice_StoresItOnce()
    {
        var user = Guid.NewGuid();
        var listen = Listen(Phone);

        Assert.Equal(1, _store.Add(user, [listen]));
        Assert.Equal(0, _store.Add(user, [listen]));

        var page = _store.GetSince(user, Mac, 0, 100);
        Assert.Single(page.Listens);
        Assert.Equal(listen, page.Listens[0]);
    }

    [Fact]
    public void GetSince_LeavesOutTheCallersOwnListens_ButStepsOverThem()
    {
        var user = Guid.NewGuid();
        var fromPhone = Listen(Phone);
        _store.Add(user, [fromPhone]);
        _store.Add(user, [Listen(Mac), Listen(Mac)]);

        var page = _store.GetSince(user, Mac, 0, 100);

        Assert.Equal([fromPhone], page.Listens);
        Assert.False(page.HasMore);

        // The cursor is past the Mac's own two rows: nothing is re-read, and a later listen is found.
        Assert.Empty(_store.GetSince(user, Mac, page.NextSince, 100).Listens);
        var later = Listen(Phone);
        _store.Add(user, [later]);
        Assert.Equal([later], _store.GetSince(user, Mac, page.NextSince, 100).Listens);
    }

    [Fact]
    public void GetSince_PagesInSequenceOrder_WithoutSkippingOrRepeating()
    {
        var user = Guid.NewGuid();
        var listens = Enumerable.Range(0, 5).Select(_ => Listen(Phone)).ToArray();
        _store.Add(user, listens);

        var first = _store.GetSince(user, Mac, 0, 2);
        Assert.True(first.HasMore);
        var second = _store.GetSince(user, Mac, first.NextSince, 2);
        Assert.True(second.HasMore);
        var third = _store.GetSince(user, Mac, second.NextSince, 2);
        Assert.False(third.HasMore);

        Assert.Equal(listens, first.Listens.Concat(second.Listens).Concat(third.Listens));
    }

    [Fact]
    public void GetSince_ALateUploadOfAnOldListen_IsStillDelivered()
    {
        var user = Guid.NewGuid();
        _store.Add(user, [Listen(Phone, playedAt: 2_000)]);
        var cursor = _store.GetSince(user, Mac, 0, 100).NextSince;

        // Played before the listen the Mac has already read, uploaded after it.
        var offline = Listen(Phone, playedAt: 1_000);
        _store.Add(user, [offline]);

        Assert.Equal([offline], _store.GetSince(user, Mac, cursor, 100).Listens);
    }

    [Fact]
    public void Listens_AreScopedToTheirUser()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var shared = Listen(Phone);

        // The same listen id under two users is two listens.
        Assert.Equal(1, _store.Add(alice, [shared]));
        Assert.Equal(1, _store.Add(bob, [shared]));
        _store.Add(alice, [Listen(Phone)]);

        Assert.Equal(2, _store.GetSince(alice, Mac, 0, 100).Listens.Count);
        Assert.Single(_store.GetSince(bob, Mac, 0, 100).Listens);

        Assert.Equal(2, _store.DeleteUser(alice));
        Assert.Empty(_store.GetSince(alice, Mac, 0, 100).Listens);
        Assert.Equal([bob], _store.GetAllUserIds());
    }

    [Fact]
    public void Epoch_SurvivesARestart_AndDiffersBetweenStores()
    {
        var epoch = _store.Epoch;
        Assert.False(string.IsNullOrEmpty(epoch));

        _store.Dispose();
        SqliteConnection.ClearAllPools();
        _store = new ListenHistoryStore(_tempDir, NullLogger.Instance);
        Assert.Equal(epoch, _store.Epoch);

        var otherDir = Path.Combine(_tempDir, "other");
        using var other = new ListenHistoryStore(otherDir, NullLogger.Instance);
        Assert.NotEqual(epoch, other.Epoch);
    }

    [Fact]
    public void ASequenceIsNeverReused_AfterItsUserIsDeleted()
    {
        var gone = Guid.NewGuid();
        var user = Guid.NewGuid();
        _store.Add(user, [Listen(Phone)]);
        _store.Add(gone, [Listen(Phone)]);
        var cursor = _store.GetSince(user, Mac, 0, 100).NextSince;
        _store.DeleteUser(gone);

        var next = Listen(Phone);
        _store.Add(user, [next]);

        Assert.Equal([next], _store.GetSince(user, Mac, cursor, 100).Listens);
    }

    [Theory]
    [InlineData(null, "11111111-1111-1111-1111-111111111111", 1000L, "album")]
    [InlineData("a", "not-a-guid", 1000L, "album")]
    [InlineData("a", "00000000-0000-0000-0000-000000000000", 1000L, "album")]
    [InlineData("a", "11111111-1111-1111-1111-111111111111", 0L, "album")]
    [InlineData("a", "11111111-1111-1111-1111-111111111111", 9000L, "album")]
    [InlineData("a", "11111111-1111-1111-1111-111111111111", 1000L, "")]
    public void TryBuildRecord_RefusesAMalformedListen(string? id, string itemId, long playedAt, string contextType)
    {
        var dto = new ListenDto { Id = id, ItemId = itemId, PlayedAt = playedAt, ContextType = contextType };

        Assert.False(ListenHistoryController.TryBuildRecord(dto, Phone, latestPlayedAt: 5000, out _));
    }

    [Fact]
    public void TryBuildRecord_TakesTheDeviceFromTheCaller_NotTheBody()
    {
        var dto = new ListenDto
        {
            Id = "a",
            ItemId = "11111111111111111111111111111111",
            PlayedAt = 1000,
            ContextType = "playlist",
            ContextId = " ",
            DeviceId = "someone-else"
        };

        Assert.True(ListenHistoryController.TryBuildRecord(dto, Phone, latestPlayedAt: 5000, out var record));
        Assert.Equal(Phone, record!.DeviceId);
        Assert.Null(record.ContextId);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), record.ItemId);
    }

    private static ListenRecord Listen(string device, long playedAt = 1_700_000_000_000)
        => new(Guid.NewGuid().ToString("D"), Guid.NewGuid(), playedAt, device, "album", Guid.NewGuid().ToString("N"));
}

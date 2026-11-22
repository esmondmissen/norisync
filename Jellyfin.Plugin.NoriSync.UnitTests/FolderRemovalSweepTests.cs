using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Jellyfin removes a folder's children with the folder and raises <c>ItemRemoved</c> for the
/// folder alone. These pin that the tracker still records a deletion for every track, album and
/// artist the folder took with it — the field case: an artist directory that lost its last album
/// turned into a plain Folder, and its old album and track stayed on every client.
/// </summary>
public sealed class FolderRemovalSweepTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly string _tempDir;
    private readonly ChangeTrackingStore _store;
    private readonly FakeLibrary _library = new();
    private readonly LibraryChangeTracker _tracker;

    public FolderRemovalSweepTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "norisync-unittests-" + Guid.NewGuid().ToString("N"));
        _store = new ChangeTrackingStore(_tempDir, NullLogger.Instance);
        _tracker = new LibraryChangeTracker(
            _library.Manager,
            FakeLibrary.Proxy<IUserDataManager>(_ => null),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<LibraryChangeTracker>.Instance,
            () => _store,
            flushDelayMs: 20,
            removalSweepDelayMs: 50);
    }

    public void Dispose()
    {
        _tracker.Dispose();
        _store.Dispose();
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
    public void AnArtistFolderRemovedWithItsAlbumRecordsTheAlbumAndTrackAsDeleted()
    {
        var artist = Guid.NewGuid();
        var oldAlbum = Guid.NewGuid();
        var oldTrack = Guid.NewGuid();
        var movedTrack = Guid.NewGuid();
        var otherAlbum = Guid.NewGuid();
        Know(TrackedItemType.Artist, artist);
        Know(TrackedItemType.Album, oldAlbum, otherAlbum);
        Know(TrackedItemType.Track, oldTrack, movedTrack);
        _library.Live[BaseItemKind.Audio] = new() { movedTrack };
        _library.Live[BaseItemKind.MusicAlbum] = new() { otherAlbum };

        _tracker.OnItemRemoved(null, new ItemChangeEventArgs { Item = new MusicArtist { Id = artist, Name = "J‐Kwon" } });

        var deleted = WaitForDeletions(3);
        Assert.Equal(new[] { artist, oldAlbum, oldTrack }.OrderBy(id => id), deleted.OrderBy(id => id));
        Assert.DoesNotContain(oldTrack, _store.GetKnownItems(TrackedItemType.Track).Keys);
        Assert.Contains(movedTrack, _store.GetKnownItems(TrackedItemType.Track).Keys);
        Assert.DoesNotContain(oldAlbum, _store.GetKnownItems(TrackedItemType.Album).Keys);
    }

    [Fact]
    public void APlainFolderRemovalSweepsToo()
    {
        var track = Guid.NewGuid();
        Know(TrackedItemType.Track, track);

        // An untracked type: the folder itself records nothing.
        _tracker.OnItemRemoved(null, new ItemChangeEventArgs { Item = new Folder { Id = Guid.NewGuid() } });

        Assert.Equal(new[] { track }, WaitForDeletions(1));
    }

    [Fact]
    public void TheSweepWaitsForARunningScan()
    {
        var track = Guid.NewGuid();
        Know(TrackedItemType.Track, track);
        _library.ScanRunning = true;

        _tracker.OnItemRemoved(null, new ItemChangeEventArgs { Item = new Folder { Id = Guid.NewGuid() } });
        Thread.Sleep(400);
        Assert.Empty(Deletions());

        _library.ScanRunning = false;
        Assert.Equal(new[] { track }, WaitForDeletions(1));
    }

    [Fact]
    public void OnlyFoldersThatHoldLibraryContentTriggerASweep()
    {
        Assert.True(LibraryChangeTracker.MayHoldLibraryContent(new MusicArtist()));
        Assert.True(LibraryChangeTracker.MayHoldLibraryContent(new MusicAlbum()));
        Assert.True(LibraryChangeTracker.MayHoldLibraryContent(new Folder()));
        Assert.False(LibraryChangeTracker.MayHoldLibraryContent(new Audio()));
        Assert.False(LibraryChangeTracker.MayHoldLibraryContent(new Playlist()));
    }

    private void Know(TrackedItemType type, params Guid[] ids)
    {
        foreach (var id in ids)
        {
            _store.UpsertKnownItem(id, type, DateTimeOffset.UtcNow);
        }
    }

    private List<Guid> Deletions()
        => _store.GetChanges(null, null, null).Changes
            .Where(c => c.ChangeType == ChangeType.Deleted)
            .Select(c => c.ItemId)
            .ToList();

    private List<Guid> WaitForDeletions(int count)
    {
        var deadline = DateTime.UtcNow + Patience;
        List<Guid> deleted;
        while ((deleted = Deletions()).Count < count && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        // Settle once more so an extra, wrong deletion would show.
        Thread.Sleep(200);
        return Deletions();
    }

    /// <summary>The live library: the ids each item kind still has, and whether a scan runs.</summary>
    private sealed class FakeLibrary
    {
        public FakeLibrary()
        {
            Manager = Proxy<ILibraryManager>(Answer);
        }

        public ILibraryManager Manager { get; }

        public Dictionary<BaseItemKind, HashSet<Guid>> Live { get; } = new();

        public volatile bool ScanRunning;

        public static T Proxy<T>(Func<(MethodInfo Method, object?[]? Args), object?> answer)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, AnsweringProxy>();
            ((AnsweringProxy)(object)proxy).Answer = answer;
            return proxy;
        }

        private object? Answer((MethodInfo Method, object?[]? Args) call)
        {
            switch (call.Method.Name)
            {
                case "get_IsScanRunning":
                    return ScanRunning;
                case "GetItemIds":
                    var query = (InternalItemsQuery)call.Args![0]!;
                    return query.IncludeItemTypes
                        .SelectMany(kind => Live.TryGetValue(kind, out var ids) ? ids : Enumerable.Empty<Guid>())
                        .ToList();
                default:
                    return call.Method.Name.StartsWith("add_", StringComparison.Ordinal)
                        || call.Method.Name.StartsWith("remove_", StringComparison.Ordinal)
                        ? null
                        : throw new NotSupportedException(call.Method.Name);
            }
        }
    }

    public class AnsweringProxy : DispatchProxy
    {
        public Func<(MethodInfo Method, object?[]? Args), object?> Answer { get; set; } = _ => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Answer((targetMethod!, args));
    }
}

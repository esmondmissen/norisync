using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Background service that listens to Jellyfin library and user-data events and records
/// changes for audio items. Events are coalesced in-memory for a short window before being
/// flushed to the change store in a single transaction, so that bulk imports don't translate
/// 1:1 into SQLite inserts.
/// </summary>
public class LibraryChangeTracker : IHostedService, IDisposable
{
    private const int FlushDelayMs = 5000;
    private const int ArtistResolutionDelayMs = 30000;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<LibraryChangeTracker> _logger;

    private readonly object _bufferLock = new();
    private readonly Dictionary<Guid, ChangeRecord> _buffer = new();
    private readonly Dictionary<Guid, SnapshotOp> _snapshotOps = new();
    private readonly List<KeyValuePair<Guid, SnapshotOp>> _pendingSnapshotOps = new();

    // Serialises snapshot application so a batch buffered during reconciliation can never be
    // overtaken by a batch that arrived after it. Always acquired *before* _bufferLock; taking
    // them in the other order anywhere would risk a deadlock.
    private readonly object _snapshotApplyLock = new();

    // Serialises artist-resolution reconciliation between startup and the debounce timer.
    private readonly object _artistResolutionLock = new();
    private Timer? _flushTimer;
    private Timer? _artistResolutionTimer;
    private bool _stopped;
    private bool _reconciling;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangeTracker"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="userDataManager">The Jellyfin user-data manager.</param>
    /// <param name="memoryCache">The memory cache the sync endpoints memoise artist lookups in.</param>
    /// <param name="logger">Logger instance.</param>
    public LibraryChangeTracker(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IMemoryCache memoryCache,
        ILogger<LibraryChangeTracker> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe before reconciling, not after. Reconciliation scans the whole library and can
        // run for minutes on a large one; a rescan kicked off at server start lands squarely
        // inside that window. Subscribing afterwards silently dropped every event fired during
        // it for the rest of the process lifetime — the deletions among them recoverable only on
        // some later restart, and not at all if the snapshot was bootstrapped in between.
        //
        // The race this ordering used to avoid is handled explicitly instead: _reconciling parks
        // snapshot ops in _pendingSnapshotOps so they cannot interleave with the ReplaceKnownItems
        // atomic rewrite, and they are drained once it has committed. Change *records* need no
        // such handling — the Changes table is append-only, so they can flow throughout.
        //
        // Subscribe under _bufferLock alongside the _stopped check. Pairs with Stop / Dispose,
        // which set _stopped=true under the same lock before calling -=. Without this, a Stop
        // racing startup would -= an empty handler list and then this would += into
        // _libraryManager (which outlives this instance for the rest of the server's lifetime),
        // leaking every change record into a disposed tracker.
        lock (_bufferLock)
        {
            if (_stopped)
            {
                return Task.CompletedTask;
            }

            _reconciling = true;

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;
            _libraryManager.ItemRemoved += OnItemRemoved;
            _userDataManager.UserDataSaved += OnUserDataSaved;
        }

        // Run reconciliation off the startup thread so the hosted-service registration returns
        // promptly.
        _ = Task.Run(
            () =>
            {
                try
                {
                    ReconcileOnStartup();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "NoriSync startup reconciliation failed; live events will still be tracked");
                }
                finally
                {
                    DrainPendingSnapshotOps();
                }

                _logger.LogInformation("NoriSync change tracking started");
            },
            CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Shutdown();
        _logger.LogInformation("NoriSync change tracking stopped");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be disposed.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
        }
    }

    private void Shutdown()
    {
        // Order matters: set _stopped under the lock first so any in-flight StartAsync
        // continuation observes the flag and bails out before subscribing. Then -= outside
        // the lock — the +/- side runs without the lock, and we don't need to hold it.
        lock (_bufferLock)
        {
            _stopped = true;

            // Reopen the direct path so the final flush below still lands. Reconciliation is
            // abandoned at this point, so there is nothing left to race with.
            _reconciling = false;

            // A pending artist-resolution pass is safe to drop: the next start compares against
            // the last recorded resolution and catches up.
            _artistResolutionTimer?.Dispose();
            _artistResolutionTimer = null;
        }

        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _userDataManager.UserDataSaved -= OnUserDataSaved;

        FlushBuffer(disposeTimer: true, finalFlush: true);
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        Enqueue(e.Item, ChangeType.Added, fromUserData: false);
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        Enqueue(e.Item, ChangeType.Modified, fromUserData: false);
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        Enqueue(e.Item, ChangeType.Deleted, fromUserData: false);
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        // PlaybackProgress fires constantly during playback; we don't want each scrub to
        // generate a change record. The other reasons (TogglePlayed, UpdateUserRating,
        // PlaybackFinished, Import) are the ones a syncing client cares about.
        if (e.SaveReason == UserDataSaveReason.PlaybackProgress)
        {
            return;
        }

        if (e.Item is not Audio)
        {
            return;
        }

        Enqueue(e.Item, ChangeType.Modified, fromUserData: true);
    }

    private void Enqueue(BaseItem item, ChangeType changeType, bool fromUserData)
    {
        var trackedType = GetTrackedItemType(item);
        if (trackedType == null)
        {
            return;
        }

        var record = new ChangeRecord
        {
            ItemId = item.Id,
            ItemName = item.Name ?? string.Empty,
            ItemType = trackedType.Value,
            ChangeType = changeType,
            Timestamp = DateTimeOffset.UtcNow
        };

        PopulateMetadata(record, item);

        SnapshotOp? snapshotOp = null;
        if (!fromUserData)
        {
            snapshotOp = changeType == ChangeType.Deleted
                ? new SnapshotOp(trackedType.Value, null, isDelete: true)
                : new SnapshotOp(trackedType.Value, new DateTimeOffset(item.DateLastSaved, TimeSpan.Zero), isDelete: false);
        }

        lock (_bufferLock)
        {
            if (_stopped)
            {
                return;
            }

            // Last write wins per item id. Events arrive in temporal order, so this naturally
            // captures the net effect: Added → Modified → Deleted collapses to Deleted; a
            // burst of Modified events collapses to one. The only oddity is Added → Deleted
            // within a single window — we propagate the Deleted, and the client gets a
            // tombstone for an id it never saw, which is harmless.
            _buffer[item.Id] = record;

            // Snapshot ops follow the same last-library-event-wins rule, but UserDataSaved
            // events do not produce one — so the prior library event's snapshot effect is
            // preserved even when a UserDataSaved is the most recent change record.
            if (snapshotOp.HasValue)
            {
                _snapshotOps[item.Id] = snapshotOp.Value;
            }

            if (_flushTimer == null)
            {
                _flushTimer = new Timer(OnFlushTimer, null, FlushDelayMs, Timeout.Infinite);
            }
        }

        _logger.LogDebug(
            "Buffered {ChangeType} for {ItemType}: {ItemName} ({ItemId})",
            changeType,
            trackedType.Value,
            item.Name,
            item.Id);
    }

    private void OnFlushTimer(object? state)
    {
        FlushBuffer(disposeTimer: true, finalFlush: false);
    }

    private void FlushBuffer(bool disposeTimer, bool finalFlush)
    {
        List<ChangeRecord>? toFlush = null;
        List<KeyValuePair<Guid, SnapshotOp>>? snapshotOps = null;

        lock (_bufferLock)
        {
            if (finalFlush)
            {
                _stopped = true;
            }

            if (_buffer.Count > 0)
            {
                toFlush = _buffer.Values.ToList();
                _buffer.Clear();
            }

            if (_snapshotOps.Count > 0)
            {
                snapshotOps = _snapshotOps.ToList();
                _snapshotOps.Clear();
            }

            if (disposeTimer && _flushTimer != null)
            {
                _flushTimer.Dispose();
                _flushTimer = null;
            }
        }

        if (toFlush == null || toFlush.Count == 0)
        {
            return;
        }

        var store = Plugin.Instance?.Store;
        if (store == null)
        {
            _logger.LogWarning("Dropping {Count} buffered changes; store unavailable", toFlush.Count);
            return;
        }

        try
        {
            store.AddChanges(toFlush);
            _logger.LogDebug("Flushed {Count} buffered changes", toFlush.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush {Count} buffered changes", toFlush.Count);
            return;
        }

        if (snapshotOps != null)
        {
            ApplySnapshotOps(store, snapshotOps);
        }

        CleanupDeletedItemAssets(toFlush);

        if (toFlush.Any(r => r.ItemType == TrackedItemType.Artist))
        {
            ScheduleArtistResolution();
        }
    }

    // Drops the plugin's own item-keyed rows for anything that was just deleted, so waveforms,
    // probed audio info and animated artwork don't outlive the item they describe. This matters
    // most after a library root-folder change: ids are derived from paths, so every item is
    // reissued and every stale row here would otherwise be orphaned permanently.
    // Best-effort — any failure is logged and ignored.
    private void CleanupDeletedItemAssets(List<ChangeRecord> flushed)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return;
        }

        foreach (var record in flushed)
        {
            if (record.ChangeType != ChangeType.Deleted)
            {
                continue;
            }

            if (record.ItemType == TrackedItemType.Album || record.ItemType == TrackedItemType.Artist)
            {
                try
                {
                    var removed = plugin.AnimatedArtworkStore.DeleteItem(record.ItemId);
                    if (removed > 0)
                    {
                        _logger.LogDebug("Removed {Count} animated artwork asset(s) for deleted {ItemType} {ItemId}", removed, record.ItemType, record.ItemId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up animated artwork for deleted {ItemType} {ItemId}", record.ItemType, record.ItemId);
                }
            }
            else if (record.ItemType == TrackedItemType.Track)
            {
                try
                {
                    plugin.FeatureStore.Delete(record.ItemId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up track features for deleted track {ItemId}", record.ItemId);
                }

                try
                {
                    plugin.AudioInfoStore.Delete(record.ItemId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up cached audio info for deleted track {ItemId}", record.ItemId);
                }
            }
        }
    }

    private void ApplySnapshotOps(ChangeTrackingStore store, List<KeyValuePair<Guid, SnapshotOp>> ops)
    {
        lock (_snapshotApplyLock)
        {
            // While startup reconciliation is scanning, park the ops instead of applying them.
            // ReconcileType finishes each type with an atomic ReplaceKnownItems rewrite that
            // would otherwise clobber a concurrent upsert — or be clobbered by one. Draining
            // afterwards gives the same end state as if the events had arrived after the scan.
            lock (_bufferLock)
            {
                if (_reconciling)
                {
                    _pendingSnapshotOps.AddRange(ops);
                    return;
                }
            }

            ApplySnapshotOpsCore(store, ops);
        }
    }

    private void ApplySnapshotOpsCore(ChangeTrackingStore store, List<KeyValuePair<Guid, SnapshotOp>> ops)
    {
        foreach (var (itemId, op) in ops)
        {
            try
            {
                if (op.IsDelete)
                {
                    store.RemoveKnownItem(itemId, op.ItemType);
                }
                else if (op.DateLastSaved.HasValue)
                {
                    store.UpsertKnownItem(itemId, op.ItemType, op.DateLastSaved.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply snapshot op for {ItemType} {ItemId}", op.ItemType, itemId);
            }
        }
    }

    /// <summary>
    /// Applies snapshot ops buffered while startup reconciliation held the KnownItems tables,
    /// then reopens the normal path.
    /// </summary>
    private void DrainPendingSnapshotOps()
    {
        // Held across the whole drain so a flush that lands the instant _reconciling clears
        // cannot apply ahead of the older ops buffered here and then be overwritten by them.
        lock (_snapshotApplyLock)
        {
            List<KeyValuePair<Guid, SnapshotOp>> pending;

            lock (_bufferLock)
            {
                _reconciling = false;

                if (_pendingSnapshotOps.Count == 0)
                {
                    return;
                }

                pending = new List<KeyValuePair<Guid, SnapshotOp>>(_pendingSnapshotOps);
                _pendingSnapshotOps.Clear();
            }

            var store = Plugin.Instance?.Store;
            if (store == null)
            {
                return;
            }

            // Replayed in arrival order, so an item added and then deleted mid-scan ends up
            // absent — matching what the live path would have done had it run directly.
            ApplySnapshotOpsCore(store, pending);

            _logger.LogInformation(
                "NoriSync applied {Count} library change(s) that arrived during startup reconciliation",
                pending.Count);
        }
    }

    private void ReconcileOnStartup()
    {
        var store = Plugin.Instance?.Store;
        if (store == null)
        {
            return;
        }

        var isFirstRun = store.CountKnownItems() == 0;
        var now = DateTimeOffset.UtcNow;
        var syntheticChanges = new List<ChangeRecord>();

        ReconcileType(store, TrackedItemType.Track, BaseItemKind.Audio, isFirstRun, now, syntheticChanges);
        ReconcileType(store, TrackedItemType.Album, BaseItemKind.MusicAlbum, isFirstRun, now, syntheticChanges);
        ReconcileType(store, TrackedItemType.Playlist, BaseItemKind.Playlist, isFirstRun, now, syntheticChanges);

        // Artists are tracked on the live event path (GetTrackedItemType) but were never
        // reconciled here, so a deletion missed while the plugin was offline was never
        // recovered, and the KnownItems rows for artists were never removed by anything.
        // MusicArtist mixes path-derived folder artists with ItemsByName entries that live
        // outside any library; both are reconciled the same way. An artist tombstone for an
        // id no client holds is harmless — clients only keep artists their albums reference.
        ReconcileType(store, TrackedItemType.Artist, BaseItemKind.MusicArtist, isFirstRun, now, syntheticChanges);

        BackfillFeatureLibraries();

        ReconcileArtistResolution(store, isFirstRun);

        if (isFirstRun)
        {
            _logger.LogInformation("NoriSync snapshot bootstrapped from current library state ({Count} items); no synthetic change records emitted", store.CountKnownItems());
            return;
        }

        if (syntheticChanges.Count == 0)
        {
            _logger.LogInformation("NoriSync startup reconciliation found no drift");
            return;
        }

        try
        {
            store.AddChanges(syntheticChanges);
            _logger.LogInformation(
                "NoriSync startup reconciliation emitted {Count} synthetic change records ({Added} added, {Modified} modified, {Deleted} deleted)",
                syntheticChanges.Count,
                syntheticChanges.Count(c => c.ChangeType == ChangeType.Added),
                syntheticChanges.Count(c => c.ChangeType == ChangeType.Modified),
                syntheticChanges.Count(c => c.ChangeType == ChangeType.Deleted));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist {Count} synthetic change records from startup reconciliation", syntheticChanges.Count);
        }
    }

    private void ReconcileType(
        ChangeTrackingStore store,
        TrackedItemType trackedType,
        BaseItemKind itemKind,
        bool isFirstRun,
        DateTimeOffset now,
        List<ChangeRecord> syntheticChanges)
    {
        var liveItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { itemKind },
            Recursive = true
        });

        var snapshot = isFirstRun
            ? new Dictionary<Guid, DateTimeOffset>()
            : store.GetKnownItems(trackedType);

        var newSnapshot = new List<(Guid ItemId, DateTimeOffset DateLastSaved)>(liveItems.Count);

        foreach (var item in liveItems)
        {
            // Playlists are tracked only when their MediaType is Audio. The InternalItemsQuery
            // for BaseItemKind.Playlist returns playlists of all media types, so we filter here
            // to match the live event path in GetTrackedItemType.
            if (trackedType == TrackedItemType.Playlist && item is Playlist playlist && playlist.MediaType != MediaType.Audio)
            {
                continue;
            }

            var itemLastSaved = new DateTimeOffset(item.DateLastSaved, TimeSpan.Zero);
            newSnapshot.Add((item.Id, itemLastSaved));

            if (isFirstRun)
            {
                continue;
            }

            if (!snapshot.TryGetValue(item.Id, out var snapshotLastSaved))
            {
                syntheticChanges.Add(BuildSyntheticChange(item, trackedType, ChangeType.Added, now));
            }
            else
            {
                // Compare at millisecond precision because that is the round-trip granularity of
                // the snapshot column; sub-millisecond ticks would produce false positives.
                if (snapshotLastSaved.ToUnixTimeMilliseconds() != itemLastSaved.ToUnixTimeMilliseconds())
                {
                    syntheticChanges.Add(BuildSyntheticChange(item, trackedType, ChangeType.Modified, now));
                }

                snapshot.Remove(item.Id);
            }
        }

        if (!isFirstRun)
        {
            // Anything left in the snapshot dict was not seen in the live scan → deleted while
            // the plugin was offline. We don't have the original item to populate metadata, so
            // emit a minimal Deleted record (id + type are enough for the client).
            foreach (var orphanedId in snapshot.Keys)
            {
                syntheticChanges.Add(new ChangeRecord
                {
                    ItemId = orphanedId,
                    ItemName = string.Empty,
                    ItemType = trackedType,
                    ChangeType = ChangeType.Deleted,
                    Timestamp = now
                });
            }
        }

        store.ReplaceKnownItems(trackedType, newSnapshot);
    }

    /// <summary>
    /// Re-sends the albums whose artist credits resolve differently from when the resolution was
    /// last recorded, then records the current one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Credits are bare name strings, so the artist one resolves to can change without the album
    /// changing at all: an artist is deleted (Jellyfin 12's validator sweeps all but one artist
    /// per clean name, and its MergeDuplicateMusicArtists migration deletes 10.11's case-only
    /// duplicates), another takes over a name or appears for a credit that resolved to nothing,
    /// or one is renamed. With no change record for the album, clients would keep the old id —
    /// or none — for good. Comparing against the recorded resolution catches every such move,
    /// live or while the plugin was offline, and re-sends only the albums crediting a name that
    /// moved. A Modified album re-sends the album with its full track list, which covers
    /// track-level credits too.
    /// </para>
    /// <para>
    /// The resolution is recorded only once the re-sends are stored, so a failure leaves the old
    /// record in place and the next pass finds the same moves again.
    /// </para>
    /// </remarks>
    private void ReconcileArtistResolution(ChangeTrackingStore store, bool isFirstRun)
    {
        lock (_artistResolutionLock)
        {
            try
            {
                var lookup = ArtistNameLookup.FromLibrary(_libraryManager);
                var previous = store.GetArtistResolution();

                List<MusicAlbum> albums;
                if (previous == null)
                {
                    // Nothing recorded yet: a fresh store has no clients to correct; an existing
                    // one gets the one-time backlog in place of a comparison.
                    albums = isFirstRun ? new List<MusicAlbum>() : FindAlbumsWithUnsettledCredits(lookup);
                }
                else
                {
                    var changed = lookup.GetChangedCredits(previous);
                    if (changed == null)
                    {
                        return;
                    }

                    albums = FindAlbumsCrediting(GetAlbumsAndTracks(), changed);
                }

                AddAlbumResends(store, albums);
                store.ReplaceArtistResolution(lookup.GetEntries());

                if (albums.Count > 0)
                {
                    _logger.LogInformation(
                        "NoriSync re-sent {Count} albums whose artist credits resolve differently",
                        albums.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NoriSync could not reconcile artist credits; will retry on the next artist change or start");
            }
        }
    }

    // Artist changes arrive in bursts — a scan's validator adds or sweeps them for minutes — and a
    // pass reads the whole library whenever a name moved, so it waits for the burst to settle.
    private void ScheduleArtistResolution()
    {
        // The sync endpoints memoise the lookup; drop it now so they resolve against the current
        // artists rather than serve the old mapping until the pass runs.
        _memoryCache.Remove(ArtistNameLookup.CacheKey);

        lock (_bufferLock)
        {
            if (_stopped)
            {
                return;
            }

            if (_artistResolutionTimer == null)
            {
                _artistResolutionTimer = new Timer(OnArtistResolutionTimer, null, ArtistResolutionDelayMs, Timeout.Infinite);
            }
            else
            {
                _artistResolutionTimer.Change(ArtistResolutionDelayMs, Timeout.Infinite);
            }
        }
    }

    private void OnArtistResolutionTimer(object? state)
    {
        var store = Plugin.Instance?.Store;
        if (store != null)
        {
            ReconcileArtistResolution(store, isFirstRun: false);
        }
    }

    /// <summary>
    /// The one-time backlog for a store with no recorded resolution: the albums crediting an
    /// artist whose credits may have reached clients wrong before resolution was recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With nothing to compare against, an artist is suspect when a credit spells it differently
    /// or when it shares its name with another artist:
    /// </para>
    /// <list type="bullet">
    /// <item>Until <see cref="ArtistNameLookup"/> fell back to Jellyfin's clean name, a credit
    /// punctuated differently from its artist was dropped ("Anne-Marie" against the
    /// MusicBrainz-named "Anne‐Marie").</item>
    /// <item>10.11 matched artist names case-sensitively, so its validator created a duplicate
    /// for every differently-cased credit ("Thirty Seconds To Mars" beside "Thirty Seconds to
    /// Mars"), and the case-insensitive lookup could resolve the pair to the duplicate — which
    /// MergeDuplicateMusicArtists then deleted.</item>
    /// <item>Between same-named artists the exact-match pick followed database order rather than
    /// a fixed tie-break.</item>
    /// </list>
    /// <para>
    /// Any credit for a suspect artist may have pointed elsewhere, so every album crediting it is
    /// re-sent — not only the ones with the odd spelling.
    /// </para>
    /// </remarks>
    private List<MusicAlbum> FindAlbumsWithUnsettledCredits(ArtistNameLookup lookup)
    {
        var items = GetAlbumsAndTracks();

        var suspects = new HashSet<Guid>();
        foreach (var item in items)
        {
            foreach (var name in GetCredits(item))
            {
                if (lookup.TryResolve(name, out var artistId, out var artistName)
                    && (!string.Equals(name, artistName, StringComparison.Ordinal) || lookup.HasNamesake(artistId)))
                {
                    suspects.Add(artistId);
                }
            }
        }

        _logger.LogInformation("NoriSync re-sending the albums of {Count} artists with unsettled credits", suspects.Count);
        return FindAlbumsCrediting(items, name => lookup.TryResolve(name, out var artistId) && suspects.Contains(artistId));
    }

    // A whole-library read; callers that need it twice share one.
    private IReadOnlyList<BaseItem> GetAlbumsAndTracks()
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum, BaseItemKind.Audio },
            Recursive = true
        });

    // The credits the sync feed resolves: album artists on an album, artists on a track.
    private static IReadOnlyList<string> GetCredits(BaseItem item) => item switch
    {
        MusicAlbum album => album.AlbumArtists ?? Array.Empty<string>(),
        Audio audio => audio.Artists ?? Array.Empty<string>(),
        _ => Array.Empty<string>()
    };

    // The albums with a credit satisfying the predicate, on the album itself or on a track.
    private static List<MusicAlbum> FindAlbumsCrediting(IReadOnlyList<BaseItem> items, Func<string, bool> predicate)
    {
        var albums = new Dictionary<Guid, MusicAlbum>();
        foreach (var item in items)
        {
            if (!GetCredits(item).Any(predicate))
            {
                continue;
            }

            var album = item as MusicAlbum ?? (item as Audio)?.AlbumEntity;
            if (album != null)
            {
                albums.TryAdd(album.Id, album);
            }
        }

        return albums.Values.ToList();
    }

    private static void AddAlbumResends(ChangeTrackingStore store, List<MusicAlbum> albums)
    {
        // The store stamps the batch as it commits; this timestamp is a placeholder.
        store.AddChanges(albums
            .Select(a => BuildSyntheticChange(a, TrackedItemType.Album, ChangeType.Modified, DateTimeOffset.UtcNow))
            .ToList());
    }

    private void BackfillFeatureLibraries()
    {
        var featureStore = Plugin.Instance?.FeatureStore;
        if (featureStore == null)
        {
            return;
        }

        IReadOnlyList<Guid> missing;
        try
        {
            missing = featureStore.GetItemIdsMissingLibraries();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NoriSync feature-library backfill scan failed");
            return;
        }

        if (missing.Count == 0)
        {
            return;
        }

        var resolved = 0;
        var orphaned = 0;
        foreach (var itemId in missing)
        {
            var item = _libraryManager.GetItemById(itemId);
            IReadOnlyCollection<Guid> libraryIds;
            if (item == null)
            {
                // Track exists in the feature store but not in the library — record an empty
                // mapping so we don't re-scan it on every startup. The next waveform run will
                // notice the missing item and the row will eventually be cleared.
                libraryIds = Array.Empty<Guid>();
                orphaned++;
            }
            else
            {
                var folders = _libraryManager.GetCollectionFolders(item);
                if (folders == null || folders.Count == 0)
                {
                    libraryIds = Array.Empty<Guid>();
                }
                else
                {
                    var ids = new List<Guid>(folders.Count);
                    foreach (var folder in folders)
                    {
                        ids.Add(folder.Id);
                    }

                    libraryIds = ids;
                }
            }

            try
            {
                if (featureStore.SetLibraries(itemId, libraryIds))
                {
                    resolved++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to backfill libraries for feature row {ItemId}", itemId);
            }
        }

        _logger.LogInformation(
            "NoriSync feature-library backfill complete: scanned={Scanned} resolved={Resolved} orphaned={Orphaned}",
            missing.Count,
            resolved,
            orphaned);
    }

    private static ChangeRecord BuildSyntheticChange(BaseItem item, TrackedItemType trackedType, ChangeType changeType, DateTimeOffset timestamp)
    {
        var record = new ChangeRecord
        {
            ItemId = item.Id,
            ItemName = item.Name ?? string.Empty,
            ItemType = trackedType,
            ChangeType = changeType,
            Timestamp = timestamp
        };

        PopulateMetadata(record, item);
        return record;
    }

    private static TrackedItemType? GetTrackedItemType(BaseItem item)
    {
        return item switch
        {
            Audio => TrackedItemType.Track,
            MusicAlbum => TrackedItemType.Album,
            MusicArtist => TrackedItemType.Artist,
            Playlist playlist when playlist.MediaType == MediaType.Audio => TrackedItemType.Playlist,
            _ => null
        };
    }

    private static void PopulateMetadata(ChangeRecord record, BaseItem item)
    {
        switch (item)
        {
            case Audio audio:
                record.AlbumName = audio.Album;
                record.ArtistNames = audio.Artists?.ToList();
                break;
            case MusicAlbum album:
                record.ArtistNames = album.AlbumArtists?.ToList();
                break;
        }
    }

    // Snapshot effect buffered alongside the change record. Library events (Add/Update/Remove)
    // produce a snapshot op; UserDataSaved events do not, because user-data changes do not bump
    // BaseItem.DateLastSaved and so should not advance the snapshot's drift-detection cursor.
    private readonly struct SnapshotOp
    {
        public SnapshotOp(TrackedItemType itemType, DateTimeOffset? dateLastSaved, bool isDelete)
        {
            ItemType = itemType;
            DateLastSaved = dateLastSaved;
            IsDelete = isDelete;
        }

        public TrackedItemType ItemType { get; }

        public DateTimeOffset? DateLastSaved { get; }

        public bool IsDelete { get; }
    }
}

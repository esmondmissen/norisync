using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Scheduled task that drops the plugin's item-keyed rows whose Jellyfin item no longer
/// exists.
/// </summary>
/// <remarks>
/// The live event path in <see cref="LibraryChangeTracker"/> already evicts these as items are
/// deleted, but it can only act on deletions it actually observed. Anything removed while the
/// plugin was offline, or during the startup reconciliation window, leaves rows behind forever.
/// Changing a library's root folder orphans the entire store at once, because Jellyfin derives
/// item ids from paths and so reissues every one of them.
/// </remarks>
public class PruneOrphansTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<PruneOrphansTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PruneOrphansTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="logger">Logger for this task.</param>
    public PruneOrphansTask(ILibraryManager libraryManager, IUserManager userManager, ILogger<PruneOrphansTask> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "NoriSync: Prune Orphaned Data";

    /// <inheritdoc />
    public string Key => "NoriSyncPruneOrphans";

    /// <inheritdoc />
    public string Description => "Removes stored waveforms, cached audio info, animated artwork and playlist cover designs belonging to items that no longer exist in the library, and the listening history and playback state of deleted users.";

    /// <inheritdoc />
    public string Category => "NoriSync";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            _logger.LogWarning("Plugin not initialized; skipping orphan prune");
            return Task.CompletedTask;
        }

        // A library scan that is mid-flight, or a media mount that has not come back yet, makes
        // every item look deleted. Pruning then would wipe the stores wholesale and force a full
        // re-analysis of the library. An empty audio query is never a legitimate reason to prune.
        var liveAudio = _libraryManager.GetCount(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true
        });

        if (liveAudio == 0)
        {
            _logger.LogWarning("NoriSync orphan prune skipped: the library reports no audio items, which usually means a scan is in progress or the media path is unavailable");
            return Task.CompletedTask;
        }

        var featureRows = Prune(
            "track features",
            plugin.FeatureStore.GetAllItemIds(),
            id => plugin.FeatureStore.Delete(id),
            cancellationToken);
        progress.Report(33);

        var audioInfoRows = Prune(
            "cached audio info",
            plugin.AudioInfoStore.GetAllItemIds(),
            id => plugin.AudioInfoStore.Delete(id),
            cancellationToken);
        progress.Report(60);

        var artworkRows = Prune(
            "animated artwork",
            plugin.AnimatedArtworkStore.GetAllItemIds(),
            id => plugin.AnimatedArtworkStore.DeleteItem(id),
            cancellationToken);
        progress.Report(90);

        var coverDesignRows = Prune(
            "playlist cover designs",
            plugin.CoverDesignStore.GetAllItemIds(),
            id => plugin.CoverDesignStore.Delete(id),
            cancellationToken);
        progress.Report(95);

        PruneDeletedUsers(plugin, cancellationToken);
        progress.Report(100);

        if (featureRows + audioInfoRows + artworkRows + coverDesignRows > 0)
        {
            _logger.LogInformation(
                "NoriSync orphan prune removed rows for {FeatureCount} track feature, {AudioInfoCount} audio info, {ArtworkCount} animated artwork and {CoverDesignCount} playlist cover design item(s) whose library items no longer exist",
                featureRows,
                audioInfoRows,
                artworkRows,
                coverDesignRows);
        }
        else
        {
            _logger.LogDebug("NoriSync orphan prune found nothing to remove");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // After the purge task's 03:30 slot, so the two never contend for the same databases.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
            MaxRuntimeTicks = TimeSpan.FromMinutes(30).Ticks
        };
    }

    // Listens are kept when their ITEM goes: a library whose root moved is reissued new ids, and
    // one re-added later would otherwise have lost its history. Only a deleted user's rows go.
    private void PruneDeletedUsers(Plugin plugin, CancellationToken cancellationToken)
    {
        var listenUsers = plugin.ListenStore.GetAllUserIds();
        var playbackUsers = plugin.PlaybackStore.GetAllUserIds();
        var deleted = DeletedUsers(listenUsers.Concat(playbackUsers), _userManager.GetUserById);
        if (deleted.Count == 0)
        {
            return;
        }

        foreach (var userId in listenUsers.Where(deleted.Contains))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var removed = plugin.ListenStore.DeleteUser(userId);
            _logger.LogInformation("Pruned {Count} listen(s) of deleted user {UserId}", removed, userId);
        }

        foreach (var userId in playbackUsers.Where(deleted.Contains))
        {
            cancellationToken.ThrowIfCancellationRequested();
            plugin.PlaybackStore.DeleteUser(userId);
            _logger.LogInformation("Pruned the playback devices of deleted user {UserId}", userId);
        }
    }

    /// <summary>
    /// The stored users Jellyfin no longer has. As with items above, if NONE of them is found
    /// the user store is taken to be unreadable, not every user deleted, and nothing is pruned.
    /// (Jellyfin has no user listing common to 10.11 and 12, so the stored ids are the sample.)
    /// </summary>
    /// <param name="storedUserIds">Every user id the stores hold rows for.</param>
    /// <param name="findUser">Jellyfin's user lookup.</param>
    /// <returns>The ids to prune; empty when there are none, or when none was found.</returns>
    internal static HashSet<Guid> DeletedUsers(IEnumerable<Guid> storedUserIds, Func<Guid, object?> findUser)
    {
        var stored = storedUserIds.ToHashSet();
        var deleted = stored.Where(id => findUser(id) == null).ToHashSet();
        return deleted.Count == stored.Count ? new HashSet<Guid>() : deleted;
    }

    private int Prune(string storeName, IReadOnlyList<Guid> storedIds, Action<Guid> delete, CancellationToken cancellationToken)
    {
        var removed = 0;

        foreach (var itemId in storedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_libraryManager.GetItemById(itemId) != null)
            {
                continue;
            }

            try
            {
                delete(itemId);
                removed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune orphaned {StoreName} row for item {ItemId}", storeName, itemId);
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Pruned {Count} orphaned {StoreName} row group(s) of {Total} stored", removed, storeName, storedIds.Count);
        }

        return removed;
    }
}

using System;
using System.Collections.Generic;
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
    private readonly ILogger<PruneOrphansTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PruneOrphansTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="logger">Logger for this task.</param>
    public PruneOrphansTask(ILibraryManager libraryManager, ILogger<PruneOrphansTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "NoriSync: Prune Orphaned Data";

    /// <inheritdoc />
    public string Key => "NoriSyncPruneOrphans";

    /// <inheritdoc />
    public string Description => "Removes stored waveforms, cached audio info, animated artwork and playlist cover designs belonging to items that no longer exist in the library.";

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

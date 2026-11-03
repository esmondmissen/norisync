using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Scheduled task that purges expired change records and idle client instances. Runs daily
/// so the change log stays bounded over time even when no client triggers a purge through
/// other means.
/// </summary>
public class PurgeChangesTask : IScheduledTask
{
    private readonly ILogger<PurgeChangesTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PurgeChangesTask"/> class.
    /// </summary>
    /// <param name="logger">Logger for this task.</param>
    public PurgeChangesTask(ILogger<PurgeChangesTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "NoriSync: Purge Expired Changes";

    /// <inheritdoc />
    public string Key => "NoriSyncPurgeChanges";

    /// <inheritdoc />
    public string Description => "Removes change records older than the configured retention window, capped by the oldest acknowledged client cursor.";

    /// <inheritdoc />
    public string Category => "NoriSync";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        var config = plugin?.Configuration;
        var store = plugin?.Store;
        var instanceStore = plugin?.InstanceStore;

        if (config == null || store == null)
        {
            _logger.LogWarning("Plugin not initialized; skipping purge");
            return Task.CompletedTask;
        }

        var now = DateTimeOffset.UtcNow;
        var retentionCutoff = now.AddDays(-config.RetentionDays);

        if (instanceStore == null)
        {
            store.PurgeOlderThan(retentionCutoff);
            progress.Report(100);
            return Task.CompletedTask;
        }

        instanceStore.RemoveIdleBefore(retentionCutoff);

        var activeInstances = instanceStore.GetActiveSince(retentionCutoff);

        DateTimeOffset cutoff;
        if (activeInstances.Count == 0)
        {
            cutoff = retentionCutoff;
        }
        else
        {
            DateTimeOffset? minAck = null;
            foreach (var instance in activeInstances)
            {
                if (instance.LastFullSyncAck == null)
                {
                    // A registered client that has not yet acked any sync — keep everything
                    // inside the retention window so they can still catch up.
                    minAck = null;
                    break;
                }

                if (minAck == null || instance.LastFullSyncAck < minAck)
                {
                    minAck = instance.LastFullSyncAck;
                }
            }

            cutoff = minAck.HasValue
                ? (minAck.Value > retentionCutoff ? minAck.Value : retentionCutoff)
                : retentionCutoff;
        }

        store.PurgeOlderThan(cutoff);
        _logger.LogInformation(
            "Purge cutoff {Cutoff} (retention ceiling {Retention}, {Active} active instances)",
            cutoff,
            retentionCutoff,
            activeInstances.Count);

        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3.5).Ticks,
            MaxRuntimeTicks = TimeSpan.FromMinutes(10).Ticks
        };
    }
}

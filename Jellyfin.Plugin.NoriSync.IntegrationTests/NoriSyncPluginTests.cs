using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.NoriSync.IntegrationTests;

[Collection("Jellyfin")]
public sealed class NoriSyncPluginTests
{
    private static readonly Guid NoriSyncGuid = Guid.Parse("11c1ffe5-e4de-4cbb-a30c-c2870eb81603");
    private const string NoriSyncTaskKey = "NoriSyncWaveformAnalysis";

    private readonly JellyfinFixture _fx;
    private readonly ITestOutputHelper _out;

    public NoriSyncPluginTests(JellyfinFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    [Fact]
    public async Task Plugin_is_loaded_and_active_on_cold_start()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var plugins = await _fx.Client.ListPluginsAsync(cts.Token);

        var norisync = plugins.SingleOrDefault(p => Guid.Parse(p.Id) == NoriSyncGuid);
        Assert.NotNull(norisync);
        Assert.Equal("Active", norisync!.Status);
    }

    [Fact]
    public async Task Scheduled_task_runs_without_restart()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var tasks = await _fx.Client.ListScheduledTasksAsync(cts.Token);
        var task = tasks.SingleOrDefault(t => t.Key == NoriSyncTaskKey);
        Assert.NotNull(task);

        var baseline = task!.LastExecutionResult?.EndTimeUtc;
        await _fx.Client.TriggerTaskAsync(task.Id, cts.Token);
        var result = await _fx.Client.WaitForTaskCompletionAsync(
            task.Id, baseline, TimeSpan.FromMinutes(1), cts.Token);

        _out.WriteLine($"Task final state: {result.State}, result: {result.LastExecutionResult?.Status}");
        if (result.LastExecutionResult?.ErrorMessage is { } err)
        {
            _out.WriteLine("Error: " + err);
            _out.WriteLine(result.LastExecutionResult.LongErrorMessage ?? string.Empty);
        }

        Assert.NotNull(result.LastExecutionResult);
        Assert.Equal("Completed", result.LastExecutionResult!.Status);
    }

    [Fact]
    public async Task Scheduled_task_runs_after_system_restart()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var before = await GetNoriSyncTaskAsync(cts.Token);
        var preBaseline = before.LastExecutionResult?.EndTimeUtc;
        await _fx.Client.TriggerTaskAsync(before.Id, cts.Token);
        var preRun = await _fx.Client.WaitForTaskCompletionAsync(
            before.Id, preBaseline, TimeSpan.FromMinutes(1), cts.Token);
        Assert.Equal("Completed", preRun.LastExecutionResult?.Status);
        _out.WriteLine($"Pre-restart run: {preRun.LastExecutionResult?.Status} at {preRun.LastExecutionResult?.EndTimeUtc:O}");

        var restartStatus = await _fx.Client.RestartServerAsync(TimeSpan.FromMinutes(2), cts.Token);
        _out.WriteLine($"POST /System/Restart returned status={restartStatus}");

        var after = await GetNoriSyncTaskAsync(cts.Token);
        var postBaseline = after.LastExecutionResult?.EndTimeUtc;
        await _fx.Client.TriggerTaskAsync(after.Id, cts.Token);
        var postRun = await _fx.Client.WaitForTaskCompletionAsync(
            after.Id, postBaseline, TimeSpan.FromMinutes(2), cts.Token);

        _out.WriteLine($"Post-restart run: {postRun.LastExecutionResult?.Status} at {postRun.LastExecutionResult?.EndTimeUtc:O}");
        Assert.NotNull(postRun.LastExecutionResult);
        Assert.Equal("Completed", postRun.LastExecutionResult!.Status);
    }

    private async Task<JellyfinApiClient.TaskInfo> GetNoriSyncTaskAsync(CancellationToken ct)
    {
        var tasks = await _fx.Client.ListScheduledTasksAsync(ct);
        return tasks.Single(t => t.Key == NoriSyncTaskKey);
    }

    // Reconciliation contract: when the plugin restarts and the snapshot already matches the
    // live library state, no synthetic Added/Modified/Deleted records should be emitted. A
    // regression here (e.g. the snapshot failing to persist, the DateLastSaved comparison
    // having sub-millisecond drift, or the first-run bootstrap path mis-detecting an empty
    // snapshot as drift) would cause TotalTrackedChanges to grow on every restart.
    [Fact]
    public async Task Reconciliation_does_not_re_emit_changes_when_library_is_stable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var beforeRestart = await StableTrackedChangesCountAsync(cts.Token);
        _out.WriteLine($"Tracked changes before restart: {beforeRestart}");

        var restartStatus = await _fx.Client.RestartServerAsync(TimeSpan.FromMinutes(2), cts.Token);
        _out.WriteLine($"POST /System/Restart returned status={restartStatus}");

        var afterRestart = await StableTrackedChangesCountAsync(cts.Token);
        _out.WriteLine($"Tracked changes after restart: {afterRestart}");

        if (afterRestart != beforeRestart)
        {
            // Surface what the reconciler actually decided before failing.
            var logs = await _fx.GetContainerLogsAsync();
            foreach (var line in logs.Split('\n'))
            {
                if (line.Contains("NoriSync", StringComparison.OrdinalIgnoreCase))
                {
                    _out.WriteLine(line);
                }
            }
        }

        Assert.Equal(beforeRestart, afterRestart);
    }

    // Polls /api/norisync/status until TotalTrackedChanges has been stable for a short window,
    // so we don't sample mid-reconciliation. Reconciliation runs on Task.Run from StartAsync,
    // concurrently with the rest of the host coming up, so /status may respond before the
    // reconcile task has finished writing any synthetic records.
    private async Task<int> StableTrackedChangesCountAsync(CancellationToken ct)
    {
        // The stability window (samples × delay) must exceed LibraryChangeTracker's
        // 5s flush debounce (FlushDelayMs): an ItemAdded fired during fixture init
        // sits in the in-memory buffer for up to 5s, and a shorter window samples a
        // baseline that a pending flush then invalidates across the restart.
        const int requiredStableSamples = 15;
        var pollDelay = TimeSpan.FromMilliseconds(400);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        var lastValue = -1;
        var stableCount = 0;
        while (DateTime.UtcNow < deadline)
        {
            var current = await _fx.Client.GetTrackedChangesCountAsync(ct);
            if (current == lastValue)
            {
                stableCount++;
                if (stableCount >= requiredStableSamples)
                {
                    return current;
                }
            }
            else
            {
                lastValue = current;
                stableCount = 1;
            }

            await Task.Delay(pollDelay, ct);
        }

        return lastValue;
    }
}

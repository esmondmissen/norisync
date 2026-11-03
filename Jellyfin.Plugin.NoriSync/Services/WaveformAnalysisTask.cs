using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Scheduled task that walks every audio item in the library and runs the local waveform
/// analyzer, persisting a compact visualization blob per track. Tracks whose waveform is already
/// fresh — based on the stored analyzer version and the source file's mtime — are skipped.
/// </summary>
public class WaveformAnalysisTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly FfmpegAudioDecoder _decoder;
    private readonly ILogger<WaveformAnalysisTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WaveformAnalysisTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="decoder">Audio decoder service.</param>
    /// <param name="logger">Logger for this task.</param>
    public WaveformAnalysisTask(
        ILibraryManager libraryManager,
        FfmpegAudioDecoder decoder,
        ILogger<WaveformAnalysisTask> logger)
    {
        _libraryManager = libraryManager;
        _decoder = decoder;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "NoriSync: Analyze Waveforms";

    /// <inheritdoc />
    public string Key => "NoriSyncWaveformAnalysis";

    /// <inheritdoc />
    public string Description => "Analyzes every track for waveform visualization data.";

    /// <inheritdoc />
    public string Category => "NoriSync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var store = Plugin.Instance?.FeatureStore;

        if (config == null || store == null)
        {
            _logger.LogWarning("Plugin not initialized; skipping audio analysis task");
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true
        });

        var total = items.Count;
        var tracker = WaveformProgressTracker.Instance;
        tracker.BeginRun(total);

        if (total == 0)
        {
            progress.Report(100);
            tracker.CompleteRun();
            return;
        }

        var cap = config.MaxTracksPerRun > 0 ? config.MaxTracksPerRun : int.MaxValue;
        var concurrency = config.AnalysisConcurrency > 0 ? config.AnalysisConcurrency : 1;
        var processed = 0;
        var skipped = 0;
        var failed = 0;
        var seen = 0;
        string? terminalError = null;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = cancellationToken
        };

        try
        {
            await Parallel.ForEachAsync(items.OfType<Audio>(), parallelOptions, async (audio, ct) =>
            {
                // Best-effort early bail when the cap is reached — may overshoot by up to
                // (concurrency - 1) tracks because in-flight workers can't be cancelled.
                if (Volatile.Read(ref processed) >= cap)
                {
                    return;
                }

                var seenNow = Interlocked.Increment(ref seen);
                tracker.IncrementSeen();
                progress.Report((double)seenNow / total * 100);

                if (string.IsNullOrEmpty(audio.Path) || !File.Exists(audio.Path))
                {
                    return;
                }

                var mtime = GetMtime(audio.Path);
                if (store.HasFreshWaveform(audio.Id, WaveformAnalyzer.Version, mtime))
                {
                    Interlocked.Increment(ref skipped);
                    tracker.IncrementSkipped();
                    return;
                }

                try
                {
                    var ok = await ProcessTrackAsync(audio, mtime, ct).ConfigureAwait(false);
                    if (ok)
                    {
                        store.IncrementLifetimeProcessed(1);
                        Interlocked.Increment(ref processed);
                        tracker.IncrementProcessed();
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                        tracker.IncrementFailed();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    tracker.IncrementFailed();
                    _logger.LogWarning(ex, "Failed to process {ItemName} ({ItemId})", audio.Name, audio.Id);
                }
            }).ConfigureAwait(false);

            progress.Report(100);
            _logger.LogInformation(
                "Waveform analysis run complete: processed={Processed} skipped={Skipped} failed={Failed} total={Total} concurrency={Concurrency}",
                processed,
                skipped,
                failed,
                total,
                concurrency);
        }
        catch (OperationCanceledException)
        {
            terminalError = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            terminalError = ex.Message;
            throw;
        }
        finally
        {
            tracker.CompleteRun(terminalError);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(4).Ticks
        };
    }

    private async Task<bool> ProcessTrackAsync(Audio audio, long mtime, CancellationToken cancellationToken)
    {
        var store = Plugin.Instance?.FeatureStore;
        if (store == null)
        {
            return false;
        }

        var samples = await _decoder.DecodeAsync(audio.Path, WaveformAnalyzer.SampleRate, cancellationToken)
            .ConfigureAwait(false);

        if (samples.Length == 0)
        {
            _logger.LogDebug("Decoded zero samples for {Name} ({Id})", audio.Name, audio.Id);
            return false;
        }

        byte[]? waveform = null;
        try
        {
            waveform = WaveformAnalyzer.Analyze(samples, WaveformAnalyzer.SampleRate);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Waveform analysis failed for {Name}", audio.Name);
        }

        var feature = new TrackFeature
        {
            ItemId = audio.Id,
            Waveform = waveform,
            WaveformVersion = waveform != null ? WaveformAnalyzer.Version : null,
            SourceMtime = mtime,
            ProcessedAt = DateTimeOffset.UtcNow
        };
        store.Upsert(feature, ResolveLibraryIds(audio));
        return true;
    }

    private static long GetMtime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private IReadOnlyCollection<Guid> ResolveLibraryIds(Audio audio)
    {
        var folders = _libraryManager.GetCollectionFolders(audio);
        if (folders == null || folders.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var ids = new List<Guid>(folders.Count);
        foreach (var folder in folders)
        {
            ids.Add(folder.Id);
        }

        return ids;
    }
}

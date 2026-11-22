using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// analyzer and the beat mix descriptor over one decode, persisting both per track. Tracks
/// whose analysis is already fresh — both stored analyzer versions and the source file's
/// mtime match — are skipped.
/// </summary>
public class WaveformAnalysisTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly FfmpegAudioDecoder _decoder;
    private readonly ILogger<WaveformAnalysisTask> _logger;
    private long _waveformTicks;
    private long _mixProfileTicks;

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
        Interlocked.Exchange(ref _waveformTicks, 0);
        Interlocked.Exchange(ref _mixProfileTicks, 0);

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
                var size = GetSize(audio.Path);
                if (store.HasFreshWaveform(audio.Id, WaveformAnalyzer.Version, MixProfileAnalyzer.StoredVersion, mtime, size))
                {
                    Interlocked.Increment(ref skipped);
                    tracker.IncrementSkipped();
                    return;
                }

                try
                {
                    var ok = await ProcessTrackAsync(audio, mtime, size, ct).ConfigureAwait(false);
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
                "Waveform analysis run complete: processed={Processed} skipped={Skipped} failed={Failed} total={Total} concurrency={Concurrency} waveformCpu={WaveformSeconds:F1}s mixProfileCpu={MixProfileSeconds:F1}s",
                processed,
                skipped,
                failed,
                total,
                concurrency,
                Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _waveformTicks)).TotalSeconds,
                Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _mixProfileTicks)).TotalSeconds);
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

    private async Task<bool> ProcessTrackAsync(Audio audio, long mtime, long size, CancellationToken cancellationToken)
    {
        var store = Plugin.Instance?.FeatureStore;
        if (store == null)
        {
            return false;
        }

        // Both analysers take the decode as it arrives, so a long mix is never held whole. One
        // that throws stops being fed; the other still finishes.
        var waveformAnalysis = new WaveformAnalyzer.Accumulator(WaveformAnalyzer.SampleRate);
        var mixAnalysis = new MixProfileAnalyzer.Accumulator(WaveformAnalyzer.SampleRate);
        var waveformFailed = false;
        var mixFailed = false;
        var decoded = await _decoder.DecodeAsync(
            audio.Path,
            WaveformAnalyzer.SampleRate,
            (buffer, count) =>
            {
                if (!waveformFailed)
                {
                    waveformFailed = !Measure(ref _waveformTicks, () => waveformAnalysis.Append(buffer.AsSpan(0, count)), audio, "Waveform");
                }

                if (!mixFailed)
                {
                    mixFailed = !Measure(ref _mixProfileTicks, () => mixAnalysis.Append(buffer.AsSpan(0, count)), audio, "Mix profile");
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (decoded == 0)
        {
            _logger.LogDebug("Decoded zero samples for {Name} ({Id})", audio.Name, audio.Id);
            return false;
        }

        byte[]? waveform = null;
        if (!waveformFailed)
        {
            Measure(ref _waveformTicks, () => waveform = waveformAnalysis.Finish(), audio, "Waveform");
        }

        // Same samples, same timeline. A null descriptor (too short / silent) is still a
        // finished analysis; only a throw leaves the version unset, to retry next run.
        byte[]? mixProfile = null;
        string? mixProfileVersion = null;
        if (!mixFailed && Measure(ref _mixProfileTicks, () => mixProfile = mixAnalysis.Finish(), audio, "Mix profile"))
        {
            mixProfileVersion = MixProfileAnalyzer.StoredVersion;
        }

        var feature = new TrackFeature
        {
            ItemId = audio.Id,
            Waveform = waveform,
            WaveformVersion = waveform != null ? WaveformAnalyzer.Version : null,
            MixProfile = mixProfile,
            MixProfileVersion = mixProfileVersion,
            SourceMtime = mtime,
            SourceSize = size >= 0 ? size : null
        };
        store.Upsert(feature, ResolveLibraryIds(audio));
        return true;
    }

    /// <summary>
    /// Runs one analysis step, adding its CPU time to <paramref name="ticks"/>; false if it threw.
    /// </summary>
    private bool Measure(ref long ticks, Action step, Audio audio, string analysis)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            step();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Analysis} analysis failed for {Name}", analysis, audio.Name);
            return false;
        }
        finally
        {
            Interlocked.Add(ref ticks, Stopwatch.GetTimestamp() - started);
        }
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

    /// <summary>The file's size in bytes, or -1 when it can't be read.</summary>
    private static long GetSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
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

using System;
using System.Threading;
using Jellyfin.Plugin.NoriSync.Models;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Process-wide, in-memory progress tracker for <see cref="WaveformAnalysisTask"/>. Holds the
/// state of the current run (or the most-recent completed run since the server started) and
/// exposes it as a <see cref="WaveformProgressSnapshot"/> for API consumers.
/// </summary>
public class WaveformProgressTracker
{
    private static readonly WaveformProgressTracker _instance = new();

    private readonly object _stateLock = new();
    private bool _isRunning;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private int _total;
    private int _seen;
    private int _processed;
    private int _skipped;
    private int _failed;
    private string? _error;

    /// <summary>
    /// Gets the process-wide singleton instance.
    /// </summary>
    public static WaveformProgressTracker Instance => _instance;

    /// <summary>
    /// Resets counters and marks a run as in-progress.
    /// </summary>
    /// <param name="total">The total number of items the task plans to visit.</param>
    public void BeginRun(int total)
    {
        lock (_stateLock)
        {
            _isRunning = true;
            _startedAt = DateTimeOffset.UtcNow;
            _finishedAt = null;
            _total = total;
            _error = null;
            Volatile.Write(ref _seen, 0);
            Volatile.Write(ref _processed, 0);
            Volatile.Write(ref _skipped, 0);
            Volatile.Write(ref _failed, 0);
        }
    }

    /// <summary>
    /// Records that one more item has been visited by the task loop.
    /// </summary>
    public void IncrementSeen() => Interlocked.Increment(ref _seen);

    /// <summary>
    /// Records that one more item had its waveform (re)computed and persisted.
    /// </summary>
    public void IncrementProcessed() => Interlocked.Increment(ref _processed);

    /// <summary>
    /// Records that one more item was skipped because its existing waveform was up-to-date.
    /// </summary>
    public void IncrementSkipped() => Interlocked.Increment(ref _skipped);

    /// <summary>
    /// Records that one more item failed waveform analysis.
    /// </summary>
    public void IncrementFailed() => Interlocked.Increment(ref _failed);

    /// <summary>
    /// Marks the current run as finished.
    /// </summary>
    /// <param name="error">Terminal error message (e.g. "cancelled" or exception text), or null for success.</param>
    public void CompleteRun(string? error = null)
    {
        lock (_stateLock)
        {
            _isRunning = false;
            _finishedAt = DateTimeOffset.UtcNow;
            _error = error;
        }
    }

    /// <summary>
    /// Returns a consistent snapshot of the tracker's current state.
    /// </summary>
    /// <returns>The snapshot.</returns>
    public WaveformProgressSnapshot Snapshot()
    {
        lock (_stateLock)
        {
            var seen = Volatile.Read(ref _seen);
            var total = _total;
            double percent;
            if (total <= 0)
            {
                percent = _isRunning ? 0 : (_startedAt.HasValue ? 100 : 0);
            }
            else
            {
                percent = Math.Clamp((double)seen / total * 100, 0, 100);
            }

            return new WaveformProgressSnapshot
            {
                IsRunning = _isRunning,
                StartedAt = _startedAt,
                FinishedAt = _finishedAt,
                Total = total,
                Seen = seen,
                Processed = Volatile.Read(ref _processed),
                Skipped = Volatile.Read(ref _skipped),
                Failed = Volatile.Read(ref _failed),
                Percent = percent,
                Error = _error
            };
        }
    }
}

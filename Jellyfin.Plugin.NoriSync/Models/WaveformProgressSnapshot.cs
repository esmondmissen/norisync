using System;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Point-in-time snapshot of the waveform analysis task's progress. Describes the currently
/// running execution, or the last run that finished since the server started.
/// </summary>
public class WaveformProgressSnapshot
{
    /// <summary>
    /// Gets or sets a value indicating whether a waveform analysis run is currently in progress.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets when the current or most-recent run started, or null if no run has
    /// been observed since server start.
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets when the most-recent run ended. Null while a run is in progress or
    /// when no run has been observed since server start.
    /// </summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// Gets or sets the total number of audio items enumerated at the start of the run.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// Gets or sets the number of items visited so far (includes processed, skipped, and failed).
    /// </summary>
    public int Seen { get; set; }

    /// <summary>
    /// Gets or sets the number of items whose waveform was (re)computed during this run.
    /// </summary>
    public int Processed { get; set; }

    /// <summary>
    /// Gets or sets the number of items whose existing waveform was up-to-date and skipped.
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Gets or sets the number of items that failed during waveform analysis.
    /// </summary>
    public int Failed { get; set; }

    /// <summary>
    /// Gets or sets the fraction of work done as a percentage (0-100).
    /// </summary>
    public double Percent { get; set; }

    /// <summary>
    /// Gets or sets a terminal error message for the last run, if any. "cancelled" when
    /// the task was aborted, otherwise the exception message.
    /// </summary>
    public string? Error { get; set; }
}

using System;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Request body for <c>POST /api/norisync/playback/report</c> (API_FORMAT §3.20): one of
/// Jellyfin's playback reports, or a <c>Cursor</c>, with where the device is in its session.
/// </summary>
public sealed class PlaybackReportRequest
{
    /// <summary>
    /// The longest <see cref="PlaySessionId"/> accepted.
    /// </summary>
    public const int MaxPlaySessionIdLength = 128;

    /// <summary>
    /// The longest <see cref="SessionId"/> accepted.
    /// </summary>
    public const int MaxSessionIdLength = 64;

    /// <summary>
    /// The largest queue accepted, in UTF-16 characters of its JSON. A 1,000-track queue is about
    /// 40 KB; the cap only stops the table being used as general storage.
    /// </summary>
    public const int MaxQueueLength = 256 * 1024;

    /// <summary>
    /// Gets or sets the device's report number.
    /// </summary>
    public long Seq { get; set; }

    /// <summary>
    /// Gets or sets which report: <c>Start</c>, <c>Progress</c>, <c>Stopped</c> or <c>Cursor</c>.
    /// </summary>
    public string? Event { get; set; }

    /// <summary>
    /// Gets or sets the session the device is on.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Gets or sets the revision of that session's queue.
    /// </summary>
    public int QueueRev { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether other devices may be offered the session. Absent is
    /// <see langword="true"/>.
    /// </summary>
    public bool? Shareable { get; set; }

    /// <summary>
    /// Gets or sets the queue for <see cref="SessionId"/> at <see cref="QueueRev"/>, when the server
    /// hasn't acknowledged that revision.
    /// </summary>
    public JsonElement? Queue { get; set; }

    /// <summary>
    /// Gets or sets the current entry in the queue.
    /// </summary>
    public int EntryIndex { get; set; }

    /// <summary>
    /// Gets or sets the item playing.
    /// </summary>
    public string? ItemId { get; set; }

    /// <summary>
    /// Gets or sets the position, in ticks (10,000 per millisecond).
    /// </summary>
    public long PositionTicks { get; set; }

    /// <summary>
    /// Gets or sets <c>playing</c>, <c>paused</c> or <c>stopped</c>: the device after this report.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Gets or sets the play session (binds the report to a transcode).
    /// </summary>
    public string? PlaySessionId { get; set; }

    /// <summary>
    /// Gets or sets how the item is played: <c>DirectPlay</c>, <c>DirectStream</c> or
    /// <c>Transcode</c>. Absent is <c>Transcode</c>, and <c>Transcode</c> with no transcoding job is
    /// passed on to Jellyfin as <c>DirectPlay</c>, as with <c>Sessions/Playing*</c>.
    /// </summary>
    public string? PlayMethod { get; set; }

    /// <summary>
    /// Gets or sets the session this device is taking over (with a playing <c>Start</c> only).
    /// </summary>
    public PlaybackTakeOverRequest? TakeOver { get; set; }

    /// <summary>
    /// Validates the body into a report.
    /// </summary>
    /// <param name="request">The body, if any.</param>
    /// <param name="report">The report, when valid.</param>
    /// <param name="error">Why it isn't, otherwise.</param>
    /// <returns><see langword="true"/> if it is a report the server can act on.</returns>
    public static bool TryParse(PlaybackReportRequest? request, out PlaybackReport? report, out string error)
    {
        report = null;
        error = string.Empty;
        if (request == null)
        {
            error = "Provide a report.";
            return false;
        }

        if (request.Seq <= 0)
        {
            error = "Seq must be positive.";
            return false;
        }

        PlaybackEvent kind;
        switch (request.Event)
        {
            case "Start": kind = PlaybackEvent.Start; break;
            case "Progress": kind = PlaybackEvent.Progress; break;
            case "Stopped": kind = PlaybackEvent.Stopped; break;
            case "Cursor": kind = PlaybackEvent.Cursor; break;
            default:
                error = "Event must be Start, Progress, Stopped or Cursor.";
                return false;
        }

        if (!IsSessionId(request.SessionId))
        {
            error = $"SessionId must be 1 to {MaxSessionIdLength} letters, digits, '-' or '_'.";
            return false;
        }

        if (request.QueueRev < 0 || request.EntryIndex < 0 || request.PositionTicks < 0)
        {
            error = "QueueRev, EntryIndex and PositionTicks can't be negative.";
            return false;
        }

        if (!Guid.TryParse(request.ItemId, out var itemId) || itemId == Guid.Empty)
        {
            error = "ItemId must be an item id.";
            return false;
        }

        PlaybackStatus status;
        switch (request.Status)
        {
            case "playing": status = PlaybackStatus.Playing; break;
            case "paused": status = PlaybackStatus.Paused; break;
            case "stopped": status = PlaybackStatus.Stopped; break;
            default:
                error = "Status must be playing, paused or stopped.";
                return false;
        }

        string? queue = null;
        if (request.Queue is { } element && element.ValueKind != JsonValueKind.Null)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = "Queue must be a JSON object.";
                return false;
            }

            queue = element.GetRawText();
            if (queue.Length > MaxQueueLength)
            {
                error = $"Queue is at most {MaxQueueLength} characters.";
                return false;
            }
        }

        if (request.PlaySessionId is { Length: > MaxPlaySessionIdLength })
        {
            error = $"PlaySessionId is at most {MaxPlaySessionIdLength} characters.";
            return false;
        }

        PlayMethod playMethod;
        switch (request.PlayMethod)
        {
            case null: playMethod = MediaBrowser.Model.Session.PlayMethod.Transcode; break;
            case "DirectPlay": playMethod = MediaBrowser.Model.Session.PlayMethod.DirectPlay; break;
            case "DirectStream": playMethod = MediaBrowser.Model.Session.PlayMethod.DirectStream; break;
            case "Transcode": playMethod = MediaBrowser.Model.Session.PlayMethod.Transcode; break;
            default:
                error = "PlayMethod must be DirectPlay, DirectStream or Transcode.";
                return false;
        }

        string? takeOver = null;
        if (request.TakeOver != null)
        {
            if (kind != PlaybackEvent.Start || status != PlaybackStatus.Playing || !IsSessionId(request.TakeOver.SessionId))
            {
                error = "TakeOver goes with a playing Start, and names a session.";
                return false;
            }

            takeOver = request.TakeOver.SessionId;
        }

        report = new PlaybackReport(
            request.Seq,
            kind,
            request.SessionId!,
            request.QueueRev,
            request.Shareable ?? true,
            queue,
            request.EntryIndex,
            itemId,
            request.PositionTicks,
            status,
            string.IsNullOrWhiteSpace(request.PlaySessionId) ? null : request.PlaySessionId,
            playMethod,
            takeOver);
        return true;
    }

    private static bool IsSessionId(string? id) =>
        id is { Length: > 0 and <= MaxSessionIdLength } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

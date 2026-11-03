using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Periodically polls Lidarr's queue and per-album statistics to update the tracked
/// status of every request in <see cref="RequestsStore"/>. Rows whose albums have finished
/// importing (or have been removed from Lidarr) are deleted — Lidarr is the source of
/// truth from that point on.
/// </summary>
public class RequestsSyncTask : IScheduledTask
{
    private readonly LidarrClient _client;
    private readonly ILogger<RequestsSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestsSyncTask"/> class.
    /// </summary>
    /// <param name="client">Lidarr forwarder.</param>
    /// <param name="logger">Logger.</param>
    public RequestsSyncTask(LidarrClient client, ILogger<RequestsSyncTask> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "NoriSync: Sync Lidarr Request Status";

    /// <inheritdoc />
    public string Key => "NoriSyncRequestsSync";

    /// <inheritdoc />
    public string Description => "Refreshes the status of tracked Lidarr requests from Lidarr's queue and album stats.";

    /// <inheritdoc />
    public string Category => "NoriSync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var store = Plugin.Instance?.RequestsStore;
        if (store == null)
        {
            _logger.LogWarning("Plugin not initialized; skipping request sync");
            return;
        }

        if (_client.TryGetBaseUri() == null)
        {
            progress.Report(100);
            return;
        }

        var trackedIds = store.GetDistinctAlbumIds();
        if (trackedIds.Count == 0)
        {
            progress.Report(100);
            return;
        }

        Dictionary<int, QueueEntry> queueByAlbumId;
        Dictionary<int, AlbumStats> albumsById;
        try
        {
            queueByAlbumId = await FetchQueueAsync(cancellationToken).ConfigureAwait(false);
            albumsById = await FetchAlbumsAsync(trackedIds, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lidarr unreachable during request sync");
            return;
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Lidarr request sync timed out");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var done = 0;

        foreach (var albumId in trackedIds)
        {
            if (queueByAlbumId.TryGetValue(albumId, out var qe))
            {
                store.UpdateStatusByAlbumId(albumId, qe.Status, qe.Progress, qe.ErrorMessage, now);
            }
            else if (!albumsById.TryGetValue(albumId, out var album))
            {
                // Not in queue and Lidarr no longer knows about the album — admin removed it.
                store.DeleteByAlbumId(albumId);
                _logger.LogInformation("Lidarr album {AlbumId} no longer exists; dropping tracked requests", albumId);
            }
            else if (album.IsFullyImported)
            {
                store.DeleteByAlbumId(albumId);
                _logger.LogInformation("Lidarr album {AlbumId} fully imported; dropping tracked requests", albumId);
            }
            else
            {
                // Album exists, no in-flight queue entry. Could be still searching for a release.
                // Leave the existing row alone; only refresh the timestamp so consumers see liveness.
                store.UpdateStatusByAlbumId(albumId, RequestStatus.Pending, null, null, now);
            }

            done++;
            progress.Report((double)done / trackedIds.Count * 100.0);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(1).Ticks,
            MaxRuntimeTicks = TimeSpan.FromMinutes(2).Ticks
        };
    }

    private async Task<Dictionary<int, QueueEntry>> FetchQueueAsync(CancellationToken cancellationToken)
    {
        var byAlbum = new Dictionary<int, QueueEntry>();

        using var upstream = await _client.SendAsync(
            HttpMethod.Get,
            "queue",
            "?pageSize=500&includeUnknownAlbumItems=false",
            body: null,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (!upstream.IsSuccessStatusCode)
        {
            return byAlbum;
        }

        var bytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return byAlbum;
        }

        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
        {
            return byAlbum;
        }

        foreach (var record in records.EnumerateArray())
        {
            if (!record.TryGetProperty("albumId", out var albumIdProp) || !albumIdProp.TryGetInt32(out var albumId))
            {
                continue;
            }

            var status = MapQueueStatus(record);
            double? progress = null;
            if (record.TryGetProperty("size", out var sizeProp)
                && record.TryGetProperty("sizeleft", out var leftProp)
                && sizeProp.TryGetDouble(out var size)
                && leftProp.TryGetDouble(out var left)
                && size > 0)
            {
                progress = Math.Clamp((size - left) / size, 0.0, 1.0);
            }

            var errorMessage = record.TryGetProperty("errorMessage", out var errProp) && errProp.ValueKind == JsonValueKind.String
                ? errProp.GetString()
                : null;

            // statusMessages carries the human-readable reason for warnings (unmapped tracks,
            // missing audio files, manual import required, …). errorMessage is usually empty
            // in those cases, so fall back to the concatenated statusMessages so callers can
            // tell the user why Lidarr is stuck.
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                errorMessage = FormatStatusMessages(record);
            }

            // First in wins; queue can occasionally have duplicate album entries
            // (e.g. retry after a failed grab), and the first record is the active one.
            byAlbum.TryAdd(albumId, new QueueEntry(status, progress, errorMessage));
        }

        return byAlbum;
    }

    private async Task<Dictionary<int, AlbumStats>> FetchAlbumsAsync(IReadOnlyList<int> albumIds, CancellationToken cancellationToken)
    {
        var byId = new Dictionary<int, AlbumStats>();
        if (albumIds.Count == 0)
        {
            return byId;
        }

        // Lidarr accepts repeated `albumIds=` query params; chunk to keep URL length sane.
        const int chunkSize = 100;
        for (var i = 0; i < albumIds.Count; i += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var end = Math.Min(i + chunkSize, albumIds.Count);
            var query = "?";
            for (var j = i; j < end; j++)
            {
                if (j > i)
                {
                    query += "&";
                }

                query += "albumIds=" + albumIds[j];
            }

            using var upstream = await _client.SendAsync(
                HttpMethod.Get,
                "album",
                query,
                body: null,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);

            if (!upstream.IsSuccessStatusCode)
            {
                continue;
            }

            var bytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var album in doc.RootElement.EnumerateArray())
            {
                if (!album.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var id))
                {
                    continue;
                }

                var (fileCount, totalCount) = ExtractTrackCounts(album);
                byId[id] = new AlbumStats(fileCount, totalCount);
            }
        }

        return byId;
    }

    private static (int FileCount, int TotalCount) ExtractTrackCounts(JsonElement album)
    {
        if (!album.TryGetProperty("statistics", out var stats) || stats.ValueKind != JsonValueKind.Object)
        {
            return (0, 0);
        }

        var file = stats.TryGetProperty("trackFileCount", out var f) && f.TryGetInt32(out var fi) ? fi : 0;
        var total = stats.TryGetProperty("totalTrackCount", out var t) && t.TryGetInt32(out var ti) ? ti : 0;
        return (file, total);
    }

    private static RequestStatus MapQueueStatus(JsonElement record)
    {
        var trackedState = record.TryGetProperty("trackedDownloadState", out var ts) && ts.ValueKind == JsonValueKind.String
            ? ts.GetString()
            : null;
        var trackedStatus = record.TryGetProperty("trackedDownloadStatus", out var tst) && tst.ValueKind == JsonValueKind.String
            ? tst.GetString()
            : null;
        var status = record.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;

        // Hard failures first: Lidarr has given up and the download/import won't progress
        // without intervention different from a warning (typically a new search or removal).
        if (string.Equals(trackedState, "failedPending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trackedState, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trackedStatus, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return RequestStatus.Failed;
        }

        // Stalled: Lidarr's UI shows this as a yellow warning triangle. Most common cause is
        // a completed download sitting in importPending because Lidarr can't map the files
        // back to tracks (bad tags, wrong release, missing audio). importBlocked is the
        // explicit "needs manual import" state. Either one needs a human, not a retry.
        if (string.Equals(trackedStatus, "warning", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trackedState, "importBlocked", StringComparison.OrdinalIgnoreCase))
        {
            return RequestStatus.Stalled;
        }

        if (string.Equals(trackedState, "importPending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trackedState, "importing", StringComparison.OrdinalIgnoreCase))
        {
            return RequestStatus.Importing;
        }

        return RequestStatus.Downloading;
    }

    private static string? FormatStatusMessages(JsonElement record)
    {
        if (!record.TryGetProperty("statusMessages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var entry in messages.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = entry.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            var detailParts = new List<string>();
            if (entry.TryGetProperty("messages", out var msgArr) && msgArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in msgArr.EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.String)
                    {
                        var text = m.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            detailParts.Add(text);
                        }
                    }
                }
            }

            var details = string.Join("; ", detailParts);
            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(details))
            {
                parts.Add(title + ": " + details);
            }
            else if (!string.IsNullOrWhiteSpace(title))
            {
                parts.Add(title);
            }
            else if (!string.IsNullOrWhiteSpace(details))
            {
                parts.Add(details);
            }
        }

        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    private readonly record struct QueueEntry(RequestStatus Status, double? Progress, string? ErrorMessage);

    private readonly record struct AlbumStats(int FileCount, int TotalCount)
    {
        public bool IsFullyImported => TotalCount > 0 && FileCount >= TotalCount;
    }
}

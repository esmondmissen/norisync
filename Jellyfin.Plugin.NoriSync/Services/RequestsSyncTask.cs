using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Periodically polls each album backend's queue and per-album statistics (Lidarr, and SoulSync's
/// Lidarr-compatible API) to update the tracked status of every album request. Rows whose albums
/// have finished importing (or have been removed) are deleted — the backend is the source of
/// truth from that point on. Also drives each pending album's search schedule
/// (<see cref="AlbumSearchScheduler"/>), and moves song requests on (<see cref="TrackRequestsSync"/>).
/// </summary>
public class RequestsSyncTask : IScheduledTask
{
    private readonly IReadOnlyList<AlbumBackend>? _backends;
    private readonly LidarrClient? _client;
    private readonly AlbumSearchScheduler? _searchScheduler;
    private readonly TrackRequestsSync? _tracks;
    private readonly ILogger<RequestsSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestsSyncTask"/> class.
    /// </summary>
    /// <param name="backends">The album backends.</param>
    /// <param name="tracks">The song request sweep.</param>
    /// <param name="logger">Logger.</param>
    public RequestsSyncTask(AlbumBackends backends, TrackRequestsSync tracks, ILogger<RequestsSyncTask> logger)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _backends = backends.All;
        _tracks = tracks;
        _logger = logger;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestsSyncTask"/> class for one Lidarr
    /// backend, for tests of <see cref="SyncAsync"/>.
    /// </summary>
    /// <param name="client">Lidarr forwarder.</param>
    /// <param name="searchScheduler">Runs the searches for requested albums.</param>
    /// <param name="logger">Logger.</param>
    internal RequestsSyncTask(LidarrClient client, AlbumSearchScheduler searchScheduler, ILogger<RequestsSyncTask> logger)
    {
        _client = client;
        _searchScheduler = searchScheduler;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "NoriSync: Sync Request Status";

    /// <inheritdoc />
    public string Key => "NoriSyncRequestsSync";

    /// <inheritdoc />
    public string Description => "Refreshes the status of album requests (Lidarr, SoulSync) and song requests (SoulSync), and searches for requested albums that haven't been found yet.";

    /// <inheritdoc />
    public string Category => "NoriSync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var plugin = Plugin.Instance;
        if (plugin == null || _backends == null)
        {
            _logger.LogWarning("Plugin not initialized; skipping request sync");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var soulSyncAlbums = plugin.SoulSyncRequestsStore.GetDistinctAlbumIds().Count > 0;
        var songs = plugin.TrackRequestsStore.ListAll().Count > 0;
        var downloads = (soulSyncAlbums || songs) && _tracks is { IsConfigured: true }
            ? await ReadSoulSyncDownloadsAsync(cancellationToken).ConfigureAwait(false)
            : null;

        foreach (var backend in _backends)
        {
            if (backend.Store is { } store)
            {
                await SyncAlbumsAsync(backend.Client, backend.Scheduler, store, now, downloads, cancellationToken).ConfigureAwait(false);
            }
        }

        if (songs && downloads != null)
        {
            try
            {
                await _tracks!.SyncAsync(plugin.TrackRequestsStore, downloads, now, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "SoulSync unreachable during the song request sync");
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "SoulSync song request sync timed out");
            }
        }

        progress.Report(100);
    }

    /// <summary>
    /// One sweep: every tracked album's status from Lidarr's queue and album stats, and the
    /// search schedule moved on for albums that are neither downloading nor imported.
    /// </summary>
    /// <param name="store">The requests store.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task completing when the sweep is done.</returns>
    internal Task SyncAsync(RequestsStore store, DateTimeOffset now, CancellationToken cancellationToken)
        => SyncAlbumsAsync(_client!, _searchScheduler!, store, now, downloads: null, cancellationToken);

    /// <summary>
    /// What SoulSync's download tasks say about an album request on it: downloading (with the
    /// mean progress of its tracks still downloading) or importing; null when none of its tracks
    /// is in flight. SoulSync's Lidarr-compatible queue is always empty, so this is the only
    /// sign of progress it gives.
    /// </summary>
    /// <param name="album">The album as the compat API's <c>GET album</c> returns it.</param>
    /// <param name="downloads">SoulSync's download tasks.</param>
    /// <returns>The status and progress, or null.</returns>
    internal static (RequestStatus Status, double? Progress)? SoulSyncAlbumActivity(JsonElement album, IReadOnlyList<SoulSyncDownload> downloads)
    {
        var title = TrackMatch.Fold(SoulSyncClient.ReadString(album, "title"));
        var artist = album.TryGetProperty("artist", out var a) ? TrackMatch.Fold(SoulSyncClient.ReadString(a, "artistName")) : string.Empty;
        if (title.Length == 0)
        {
            return null;
        }

        var tasks = downloads.Where(d => TrackMatch.Fold(d.Album) == title
            && (artist.Length == 0 || TrackMatch.Fold(d.Artist).Contains(artist, StringComparison.Ordinal) || artist.Contains(TrackMatch.Fold(d.Artist), StringComparison.Ordinal)))
            .ToList();
        var downloading = tasks.Where(d => d.Status == "downloading").ToList();
        if (downloading.Count > 0)
        {
            return (RequestStatus.Downloading, downloading.Average(d => d.Progress));
        }

        return tasks.Any(d => d.Status == "post_processing") ? (RequestStatus.Importing, null) : null;
    }

    private async Task<IReadOnlyList<SoulSyncDownload>?> ReadSoulSyncDownloadsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _tracks!.ReadDownloadsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "SoulSync unreachable during request sync");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "SoulSync downloads read timed out");
        }

        return null;
    }

    private async Task SyncAlbumsAsync(
        LidarrClient client,
        AlbumSearchScheduler scheduler,
        RequestsStore store,
        DateTimeOffset now,
        IReadOnlyList<SoulSyncDownload>? downloads,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (client.TryGetBaseUri() == null)
        {
            return;
        }

        var trackedIds = store.GetDistinctAlbumIds();
        if (trackedIds.Count == 0)
        {
            return;
        }

        try
        {
            var queueByAlbumId = await FetchQueueAsync(client, cancellationToken).ConfigureAwait(false);
            var albumsById = await FetchAlbumsAsync(client, trackedIds, cancellationToken).ConfigureAwait(false);
            var searchesLeft = AlbumSearchScheduler.SearchesPerSweep;

            foreach (var albumId in trackedIds)
            {
                if (queueByAlbumId.TryGetValue(albumId, out var qe))
                {
                    store.UpdateStatusByAlbumId(albumId, qe.Status, qe.Progress, qe.ErrorMessage, now);
                }
                else if (!albumsById.TryGetValue(albumId, out var album))
                {
                    // Not in queue and the backend no longer knows about the album — admin removed it.
                    store.DeleteByAlbumId(albumId);
                    _logger.LogInformation("{Backend} album {AlbumId} no longer exists; dropping tracked requests", client.Backend, albumId);
                }
                else if (album.IsFullyImported)
                {
                    store.DeleteByAlbumId(albumId);
                    _logger.LogInformation("{Backend} album {AlbumId} fully imported; dropping tracked requests", client.Backend, albumId);
                }
                else
                {
                    var activity = client.Backend == RequestBackend.SoulSync && downloads != null
                        ? SoulSyncAlbumActivity(album.Json, downloads)
                        : null;
                    store.UpdateStatusByAlbumId(albumId, activity?.Status ?? RequestStatus.Pending, activity?.Progress, null, now);

                    var searched = await scheduler.AdvanceStoredAsync(
                        store,
                        albumId,
                        album.Json,
                        now,
                        maySearch: searchesLeft > 0,
                        cancellationToken).ConfigureAwait(false);
                    if (searched)
                    {
                        searchesLeft--;
                    }
                }
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "{Backend} unreachable during request sync", client.Backend);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "{Backend} request sync timed out", client.Backend);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "{Backend} answered the request sync with something that isn't JSON", client.Backend);
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

    private static async Task<Dictionary<int, QueueEntry>> FetchQueueAsync(LidarrClient client, CancellationToken cancellationToken)
    {
        var byAlbum = new Dictionary<int, QueueEntry>();

        using var upstream = await client.SendAsync(
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

    private static async Task<Dictionary<int, AlbumStats>> FetchAlbumsAsync(LidarrClient client, IReadOnlyList<int> albumIds, CancellationToken cancellationToken)
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

            using var upstream = await client.SendAsync(
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
                byId[id] = new AlbumStats(fileCount, totalCount, album.Clone());
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

    private readonly record struct AlbumStats(int FileCount, int TotalCount, JsonElement Json)
    {
        public bool IsFullyImported => TotalCount > 0 && FileCount >= TotalCount;
    }
}

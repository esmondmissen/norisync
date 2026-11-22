using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Runs the searches for requested albums itself instead of trusting Lidarr to. Lidarr's
/// <c>searchForNewAlbum</c> never fires for a new artist, a search sent before the album's
/// releases load rejects every sized result ("Album duration is 0") yet reports success, and
/// Lidarr never searches a miss again (only RSS sees new uploads). So each album waits until
/// its releases are in, is searched, has the result read back, and is searched again on a
/// backoff for <see cref="RetryWindow"/>. Full rationale: API_FORMAT.md §3.16.
/// On SoulSync's Lidarr-compatible API an <c>AlbumSearch</c> puts the album's tracks on
/// SoulSync's wishlist, which then retries on its own: there is no metadata to wait for, the
/// result reads "N tracks queued", and an album queued is not searched again.
/// </summary>
public sealed partial class AlbumSearchScheduler : IDisposable
{
    /// <summary>
    /// The most searches one sweep sends, so a backlog of requests doesn't hit every indexer
    /// at once; the rest go on the following minutes.
    /// </summary>
    internal const int SearchesPerSweep = 5;

    /// <summary>
    /// How long a request keeps being searched for after it was made (or "Search Again").
    /// </summary>
    public static readonly TimeSpan RetryWindow = TimeSpan.FromDays(14);

    /// <summary>
    /// How long a search waits for Lidarr to load the album's releases before it is sent
    /// anyway: an album MusicBrainz has no durations for would otherwise never be searched.
    /// </summary>
    internal static readonly TimeSpan MetadataPatience = TimeSpan.FromHours(1);

    /// <summary>
    /// Where an interactive grab leaves the schedule: searched again only if nothing has
    /// arrived by then (a failed download Lidarr dropped from its queue).
    /// </summary>
    internal static readonly TimeSpan AfterGrabRecheck = TimeSpan.FromHours(6);

    /// <summary>
    /// The gap after the n-th search (the last entry repeats).
    /// </summary>
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
    };

    private readonly LidarrClient _client;
    private readonly ILogger<AlbumSearchScheduler> _logger;

    /// <summary>
    /// Serialises the sweep and "Search Again" so one album never gets two searches from them.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="AlbumSearchScheduler"/> class.
    /// </summary>
    /// <param name="client">The Lidarr client.</param>
    /// <param name="logger">Logger instance.</param>
    public AlbumSearchScheduler(LidarrClient client, ILogger<AlbumSearchScheduler> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Gets what this scheduler's searches go to.
    /// </summary>
    public RequestBackend Backend => _client.Backend;

    private string BackendName => Backend == RequestBackend.SoulSync ? "SoulSync" : "Lidarr";

    /// <summary>
    /// A new schedule: due now, waiting for the album's releases.
    /// </summary>
    /// <param name="albumId">The Lidarr album id.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The schedule.</returns>
    public static AlbumSearchRecord Fresh(int albumId, DateTimeOffset now) => new()
    {
        LidarrAlbumId = albumId,
        State = AlbumSearchState.AwaitingMetadata,
        Attempts = 0,
        WindowStart = now,
        NextSearchAt = now,
    };

    /// <summary>
    /// The schedule after an interactive grab: nothing searched until <see cref="AfterGrabRecheck"/>.
    /// </summary>
    /// <param name="albumId">The Lidarr album id.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The schedule.</returns>
    public static AlbumSearchRecord AfterGrab(int albumId, DateTimeOffset now) => new()
    {
        LidarrAlbumId = albumId,
        State = AlbumSearchState.Grabbed,
        Attempts = 0,
        WindowStart = now,
        NextSearchAt = now + AfterGrabRecheck,
    };

    /// <summary>
    /// When the next search is due after <paramref name="record"/>'s latest, or null once that
    /// would fall past the retry window.
    /// </summary>
    /// <param name="record">The schedule, its <see cref="AlbumSearchRecord.Attempts"/> counting the latest search.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The next due time, or null.</returns>
    internal static DateTimeOffset? NextAfter(AlbumSearchRecord record, DateTimeOffset now)
    {
        var index = Math.Clamp(record.Attempts - 1, 0, Backoff.Length - 1);
        var next = now + Backoff[index];
        return next > record.WindowStart + RetryWindow ? null : next;
    }

    /// <summary>
    /// Whether a search now could match results: Lidarr has loaded a release with a length.
    /// With none, its size check rejects every result that has a size.
    /// </summary>
    /// <param name="album">The album as Lidarr's <c>GET /album</c> returns it.</param>
    /// <returns>True when the album has a release with a duration.</returns>
    internal static bool IsReadyToSearch(JsonElement album)
    {
        if (album.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (album.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array)
        {
            foreach (var release in releases.EnumerateArray())
            {
                if (release.ValueKind == JsonValueKind.Object
                    && release.TryGetProperty("duration", out var duration)
                    && duration.TryGetInt64(out var ms)
                    && ms > 0)
                {
                    return true;
                }
            }
        }

        return album.TryGetProperty("duration", out var albumDuration)
            && albumDuration.TryGetInt64(out var albumMs)
            && albumMs > 0;
    }

    /// <summary>
    /// Reads "N reports downloaded" out of a finished <c>AlbumSearch</c> command's message.
    /// </summary>
    /// <param name="message">The command's final message.</param>
    /// <returns>N, or null when the message isn't the completion line.</returns>
    internal static int? ReportsDownloaded(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var match = ReportsDownloadedPattern().Match(message);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    /// <summary>
    /// Reads "N tracks queued" out of a finished <c>AlbumSearch</c> on SoulSync's
    /// Lidarr-compatible API: how many of the album's tracks went onto its wishlist.
    /// </summary>
    /// <param name="message">The command's final message.</param>
    /// <returns>N, or null when the message isn't that line.</returns>
    internal static int? TracksQueued(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var match = TracksQueuedPattern().Match(message);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    /// <summary>
    /// Replaces an album's schedule (a new request, "Search Again", an interactive grab), under
    /// the same lock as the sweep so a sweep in progress can't write its older copy back.
    /// </summary>
    /// <param name="store">The requests store.</param>
    /// <param name="record">The new schedule.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task completing once it is stored.</returns>
    public async Task RestartAsync(RequestsStore store, AlbumSearchRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            store.UpsertSearch(record);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <see cref="AdvanceAsync"/> on the album's stored schedule (a new one if it has none — a
    /// request from before the plugin searched itself), read and written back under the lock.
    /// </summary>
    /// <param name="store">The requests store.</param>
    /// <param name="albumId">The Lidarr album id.</param>
    /// <param name="album">The album as Lidarr's <c>GET /album</c> returns it.</param>
    /// <param name="now">The current time.</param>
    /// <param name="maySearch">False when this sweep has sent its share of searches.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether a search was sent.</returns>
    public async Task<bool> AdvanceStoredAsync(
        RequestsStore store,
        int albumId,
        JsonElement album,
        DateTimeOffset now,
        bool maySearch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = store.GetSearch(albumId) ?? Fresh(albumId, now);
            var (advanced, searched) = await AdvanceCoreAsync(record, album, now, maySearch, cancellationToken).ConfigureAwait(false);
            store.UpsertSearch(advanced);
            return searched;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Moves one album's schedule on: reads the result of a search in flight, then sends the
    /// next search if one is due. Only for an album Lidarr has, not in its download queue and
    /// not fully imported — the caller decides that.
    /// </summary>
    /// <param name="record">The album's schedule.</param>
    /// <param name="album">The album as Lidarr's <c>GET /album</c> returns it.</param>
    /// <param name="now">The current time.</param>
    /// <param name="maySearch">False when this sweep has sent its share of searches.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated schedule, and whether a search was sent.</returns>
    public async Task<(AlbumSearchRecord Record, bool Searched)> AdvanceAsync(
        AlbumSearchRecord record,
        JsonElement album,
        DateTimeOffset now,
        bool maySearch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AdvanceCoreAsync(record, album, now, maySearch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(AlbumSearchRecord Record, bool Searched)> AdvanceCoreAsync(
        AlbumSearchRecord record,
        JsonElement album,
        DateTimeOffset now,
        bool maySearch,
        CancellationToken cancellationToken)
    {
        if (record.State == AlbumSearchState.Searching)
        {
            var finished = await ReadSearchResultAsync(record, now, cancellationToken).ConfigureAwait(false);
            if (!finished)
            {
                return (record, false);
            }
        }

        if (record.NextSearchAt is not { } due || due > now || !maySearch)
        {
            return (record, false);
        }

        // SoulSync reads the tracklist from MusicBrainz when it searches; its albums never
        // carry releases, so waiting for them would hold every search for MetadataPatience.
        if (Backend == RequestBackend.Lidarr && !IsReadyToSearch(album) && now - record.WindowStart < MetadataPatience)
        {
            record.State = AlbumSearchState.AwaitingMetadata;
            return (record, false);
        }

        var commandId = await SendSearchAsync(record.LidarrAlbumId, cancellationToken).ConfigureAwait(false);
        if (commandId == null)
        {
            // Lidarr refused the command; try again on a later sweep rather than spend an attempt.
            record.NextSearchAt = now + Backoff[0];
            record.LastMessage = BackendName + " didn't accept the search.";
            return (record, false);
        }

        record.State = AlbumSearchState.Searching;
        record.CommandId = commandId;
        record.Attempts++;
        record.LastSearchedAt = now;
        record.NextSearchAt = null;
        record.LastMessage = null;
        _logger.LogInformation(
            "Searching Lidarr album {AlbumId} (attempt {Attempt}, command {CommandId})",
            record.LidarrAlbumId,
            record.Attempts,
            commandId);
        return (record, true);
    }

    /// <summary>
    /// Reads the search in flight. True once it has finished (the record then says how, and
    /// when the next one is due); false while Lidarr is still running it.
    /// </summary>
    private async Task<bool> ReadSearchResultAsync(AlbumSearchRecord record, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string? status = null;
        string? message = null;
        if (record.CommandId is int commandId)
        {
            using var response = await _client.SendAsync(
                HttpMethod.Get,
                "command/" + commandId.ToString(CultureInfo.InvariantCulture),
                queryString: null,
                body: null,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(bytes);
                status = ReadString(doc.RootElement, "status");
                message = ReadString(doc.RootElement, "message");
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
            {
                // Lidarr hiccup: ask again next sweep.
                return false;
            }
        }

        if (status is "queued" or "started")
        {
            return false;
        }

        record.CommandId = null;
        record.NextSearchAt = NextAfter(record, now);

        var reports = status != "completed" ? null
            : Backend == RequestBackend.SoulSync ? TracksQueued(message) : ReportsDownloaded(message);
        if (reports > 0)
        {
            record.State = AlbumSearchState.Grabbed;
            record.LastMessage = message;
            if (Backend == RequestBackend.SoulSync)
            {
                // SoulSync's wishlist retries the tracks itself; searching again re-adds them.
                record.NextSearchAt = null;
            }
        }
        else
        {
            // Completed with 0, or failed / aborted / purged (404): nothing was sent either way.
            record.State = AlbumSearchState.NothingFound;
            record.LastMessage = status == "completed" ? message : BackendName + "'s search didn't finish.";
        }

        _logger.LogInformation(
            "Lidarr album {AlbumId} search {Outcome}: {Message}; next {Next}",
            record.LidarrAlbumId,
            record.State,
            record.LastMessage,
            record.NextSearchAt);
        return true;
    }

    /// <summary>
    /// Queues an <c>AlbumSearch</c>. Returns its command id, or null when Lidarr refused it.
    /// </summary>
    private async Task<int?> SendSearchAsync(int albumId, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["name"] = "AlbumSearch",
            ["albumIds"] = new JsonArray(albumId)
        };
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()));
        content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(
            HttpMethod.Post,
            "command",
            queryString: null,
            content,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Lidarr AlbumSearch for album {AlbumId} returned {Status}", albumId, (int)response.StatusCode);
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("id", out var id)
            && id.TryGetInt32(out var value)
            ? value
            : null;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    [GeneratedRegex(@"(\d+) reports? downloaded", RegexOptions.CultureInvariant)]
    private static partial Regex ReportsDownloadedPattern();

    [GeneratedRegex(@"(\d+) tracks? queued", RegexOptions.CultureInvariant)]
    private static partial Regex TracksQueuedPattern();
}

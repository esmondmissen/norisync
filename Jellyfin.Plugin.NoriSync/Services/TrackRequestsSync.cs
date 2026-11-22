using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Moves song requests on, once a minute from <see cref="RequestsSyncTask"/>. In order: in the
/// Jellyfin library → done (removed); a SoulSync download task for it → that task's state; on the
/// wishlist → queued, with SoulSync's last failure; off the wishlist with neither → importing for
/// <see cref="MissingGrace"/> (SoulSync drops the row once the file is published, before Jellyfin
/// has scanned it), then failed. One wishlist read and one downloads read per sweep, never one
/// per song.
/// </summary>
public sealed class TrackRequestsSync
{
    /// <summary>
    /// How long a song off the wishlist and in no download may take to reach the library.
    /// SoulSync asks Jellyfin to scan about a minute after a batch completes.
    /// </summary>
    internal static readonly TimeSpan MissingGrace = TimeSpan.FromHours(1);

    /// <summary>
    /// The wishlist page size: SoulSync's maximum.
    /// </summary>
    private const int WishlistPage = 200;

    /// <summary>
    /// The most wishlist pages read per sweep.
    /// </summary>
    private const int MaxWishlistPages = 25;

    private readonly SoulSyncClient _soulSync;
    private readonly ITrackLibrary _library;
    private readonly ILogger<TrackRequestsSync> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackRequestsSync"/> class.
    /// </summary>
    /// <param name="soulSync">The SoulSync client.</param>
    /// <param name="library">The Jellyfin library.</param>
    /// <param name="logger">Logger.</param>
    public TrackRequestsSync(SoulSyncClient soulSync, ITrackLibrary library, ILogger<TrackRequestsSync> logger)
    {
        _soulSync = soulSync;
        _library = library;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether SoulSync is set up.
    /// </summary>
    public bool IsConfigured => _soulSync.IsConfigured;

    /// <summary>
    /// SoulSync's download tasks, newest first; empty when it can't be read.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tasks.</returns>
    /// <exception cref="HttpRequestException">SoulSync couldn't be reached.</exception>
    public async Task<IReadOnlyList<SoulSyncDownload>> ReadDownloadsAsync(CancellationToken cancellationToken)
    {
        var reply = await _soulSync.SendAsync(HttpMethod.Get, "downloads", "?limit=500", null, cancellationToken).ConfigureAwait(false);
        return reply.IsSuccess ? SoulSyncDownload.Read(reply.Data) : Array.Empty<SoulSyncDownload>();
    }

    /// <summary>
    /// One sweep over every song request.
    /// </summary>
    /// <param name="store">The song requests.</param>
    /// <param name="downloads">SoulSync's download tasks (<see cref="ReadDownloadsAsync"/>).</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task completing when the sweep is done.</returns>
    /// <exception cref="HttpRequestException">SoulSync couldn't be reached.</exception>
    public async Task SyncAsync(TrackRequestsStore store, IReadOnlyList<SoulSyncDownload> downloads, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(downloads);

        var byRecording = store.ListAll().GroupBy(r => r.RecordingMbid).ToList();
        if (byRecording.Count == 0)
        {
            return;
        }

        var wishlist = await ReadWishlistAsync(cancellationToken).ConfigureAwait(false);
        if (wishlist == null)
        {
            // An unreadable wishlist would read as "every song left it": leave them as they are.
            return;
        }

        foreach (var group in byRecording)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = group.OrderBy(r => r.SubmittedAt).First();
            var since = group.Min(r => r.SubmittedAt);

            if (_library.Contains(row.RecordingMbid, row.Title, row.ArtistName, since))
            {
                store.DeleteByRecording(row.RecordingMbid);
                _logger.LogInformation("Requested song {Mbid} ({Title}) is in the library; dropping its requests", row.RecordingMbid, row.Title);
                continue;
            }

            var next = Next(row, wishlist, downloads, now);
            store.UpdateStatus(row.RecordingMbid, next.Status, next.Progress, next.Error, next.RetryCount, next.LastAttemptAt, next.MissingSince, now);
        }
    }

    /// <summary>
    /// Where one song stands, from SoulSync's download tasks and wishlist.
    /// </summary>
    /// <param name="row">The song's request (its first).</param>
    /// <param name="wishlist">The wishlist, by stored key.</param>
    /// <param name="downloads">The download tasks, newest first.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The new state.</returns>
    internal static TrackState Next(TrackRequestRecord row, IReadOnlyDictionary<string, WishlistEntry> wishlist, IReadOnlyList<SoulSyncDownload> downloads, DateTimeOffset now)
    {
        wishlist.TryGetValue(row.SoulSyncTrackId ?? row.RecordingMbid, out var wish);
        var task = row.Title != null && row.ArtistName != null
            ? downloads.FirstOrDefault(d => d.Status != "cancelled" && TrackMatch.SameSong(d.Title, d.Artist == null ? null : new[] { d.Artist }, row.Title, row.ArtistName))
            : null;

        if (task != null)
        {
            var status = task.Status switch
            {
                "downloading" => TrackRequestStatus.Downloading,
                "post_processing" or "completed" => TrackRequestStatus.Importing,
                "failed" or "not_found" => TrackRequestStatus.Failed,
                _ => TrackRequestStatus.Searching,
            };
            return new TrackState(
                status,
                status == TrackRequestStatus.Downloading ? task.Progress : null,
                status == TrackRequestStatus.Failed ? task.Error ?? (task.Status == "not_found" ? "SoulSync found no match." : "The download failed.") : null,
                wish?.RetryCount ?? row.RetryCount,
                wish?.LastAttempted ?? row.LastAttemptAt,
                null);
        }

        if (wish != null)
        {
            return new TrackState(
                TrackRequestStatus.Queued,
                null,
                wish.RetryCount > 0 ? wish.FailureReason : null,
                wish.RetryCount,
                wish.LastAttempted,
                null);
        }

        var missingSince = row.WishlistMissingSince ?? now;
        return now - missingSince < MissingGrace
            ? new TrackState(TrackRequestStatus.Importing, null, null, row.RetryCount, row.LastAttemptAt, missingSince)
            : new TrackState(TrackRequestStatus.Failed, null, "The song left SoulSync's wishlist without reaching the library.", row.RetryCount, row.LastAttemptAt, missingSince);
    }

    /// <summary>
    /// Reads a <c>GET wishlist</c> page's rows.
    /// </summary>
    /// <param name="data">The envelope's <c>data</c>.</param>
    /// <returns>The rows.</returns>
    internal static List<WishlistEntry> ReadWishlistPage(JsonElement? data)
    {
        var rows = new List<WishlistEntry>();
        if (data is not { ValueKind: JsonValueKind.Object } body
            || !body.TryGetProperty("tracks", out var tracks)
            || tracks.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var track in tracks.EnumerateArray())
        {
            var id = SoulSyncClient.ReadString(track, "track_id") ?? SoulSyncClient.ReadString(track, "spotify_track_id");
            if (id == null)
            {
                continue;
            }

            var retries = track.TryGetProperty("retry_count", out var r) && r.TryGetInt32(out var count) ? count : 0;
            var attempted = DateTimeOffset.TryParse(SoulSyncClient.ReadString(track, "last_attempted"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : (DateTimeOffset?)null;
            rows.Add(new WishlistEntry(id, retries, attempted, SoulSyncClient.ReadString(track, "failure_reason")));
        }

        return rows;
    }

    // Null when a page couldn't be read.
    private async Task<Dictionary<string, WishlistEntry>?> ReadWishlistAsync(CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, WishlistEntry>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= MaxWishlistPages; page++)
        {
            var reply = await _soulSync.SendAsync(
                HttpMethod.Get,
                "wishlist",
                string.Format(CultureInfo.InvariantCulture, "?page={0}&limit={1}", page, WishlistPage),
                null,
                cancellationToken).ConfigureAwait(false);
            if (!reply.IsSuccess)
            {
                _logger.LogWarning("SoulSync wishlist page {Page} answered {Status}", page, (int)reply.Status);
                return null;
            }

            var rows = ReadWishlistPage(reply.Data);
            foreach (var row in rows)
            {
                byId.TryAdd(row.TrackId, row);
            }

            if (rows.Count < WishlistPage)
            {
                return byId;
            }
        }

        return byId;
    }

    /// <summary>
    /// One wishlist row.
    /// </summary>
    /// <param name="TrackId">The key it is stored under.</param>
    /// <param name="RetryCount">How many times SoulSync has tried it.</param>
    /// <param name="LastAttempted">When it last tried.</param>
    /// <param name="FailureReason">Why the last try failed.</param>
    internal sealed record WishlistEntry(string TrackId, int RetryCount, DateTimeOffset? LastAttempted, string? FailureReason);

    /// <summary>
    /// A song's new state.
    /// </summary>
    /// <param name="Status">The status.</param>
    /// <param name="Progress">Download progress.</param>
    /// <param name="Error">The reason it failed.</param>
    /// <param name="RetryCount">SoulSync's tries.</param>
    /// <param name="LastAttemptAt">SoulSync's last try.</param>
    /// <param name="MissingSince">Since when it's been off the wishlist.</param>
    internal sealed record TrackState(TrackRequestStatus Status, double? Progress, string? Error, int? RetryCount, DateTimeOffset? LastAttemptAt, DateTimeOffset? MissingSince);
}

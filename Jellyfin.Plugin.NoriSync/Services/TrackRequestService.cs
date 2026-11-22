using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Song requests: a MusicBrainz recording put on SoulSync's wishlist (<c>POST wishlist</c>, then
/// <c>POST wishlist/process</c>). The wishlist is what tags, files and imports a download and
/// retries a miss; <c>POST /api/v1/request</c> only transfers a file and isn't used. Full
/// rationale: API_FORMAT.md §3.21.
/// </summary>
public sealed class TrackRequestService
{
    private readonly SoulSyncClient _soulSync;
    private readonly MusicBrainzClient? _musicBrainz;
    private readonly Func<TrackRequestsStore?> _store;
    private readonly ILogger<TrackRequestService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackRequestService"/> class.
    /// </summary>
    /// <param name="soulSync">The SoulSync client.</param>
    /// <param name="musicBrainz">Fills in what the app didn't send.</param>
    /// <param name="logger">Logger.</param>
    public TrackRequestService(SoulSyncClient soulSync, MusicBrainzClient musicBrainz, ILogger<TrackRequestService> logger)
        : this(soulSync, musicBrainz, () => Plugin.Instance?.TrackRequestsStore, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackRequestService"/> class on a given store,
    /// for tests. With no MusicBrainz client, hints are all there is.
    /// </summary>
    /// <param name="soulSync">The SoulSync client.</param>
    /// <param name="musicBrainz">The MusicBrainz client, or null.</param>
    /// <param name="store">The store.</param>
    /// <param name="logger">Logger.</param>
    internal TrackRequestService(SoulSyncClient soulSync, MusicBrainzClient? musicBrainz, Func<TrackRequestsStore?> store, ILogger<TrackRequestService> logger)
    {
        _soulSync = soulSync;
        _musicBrainz = musicBrainz;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether SoulSync is set up.
    /// </summary>
    public bool IsConfigured => _soulSync.IsConfigured;

    /// <summary>
    /// Gets the store, or null before the plugin is up.
    /// </summary>
    public TrackRequestsStore? Store => _store();

    /// <summary>
    /// The recording id in its stored form, or null when it isn't a MusicBrainz id.
    /// </summary>
    /// <param name="value">The id as sent.</param>
    /// <returns>The lower-case id, or null.</returns>
    public static string? NormalizeMbid(string? value)
        => Guid.TryParse(value, out var id) ? id.ToString("D", CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// The <c>track_data</c> SoulSync's wishlist takes for a MusicBrainz recording. <c>source</c>
    /// and <c>provider</c> say whose ids these are: without them SoulSync reads the id as a
    /// Spotify id. The album has no <c>id</c>: a MusicBrainz album id there is a release, not
    /// the release group the app knows, and with none SoulSync keys the row by the recording.
    /// </summary>
    /// <param name="recordingMbid">The recording.</param>
    /// <param name="track">Its title, artist and album.</param>
    /// <returns>The payload.</returns>
    internal static JsonObject BuildTrackData(string recordingMbid, TrackRequestHints track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var title = track.Title!.Trim();
        var albumTitle = string.IsNullOrWhiteSpace(track.Album) ? null : track.Album.Trim();
        var albumType = string.IsNullOrWhiteSpace(track.AlbumType)
            ? (albumTitle == null ? "single" : "album")
            : track.AlbumType.Trim().ToLowerInvariant();

        return new JsonObject
        {
            ["id"] = recordingMbid,
            ["name"] = title,
            ["artists"] = new JsonArray(new JsonObject { ["name"] = track.Artist!.Trim() }),
            ["album"] = new JsonObject
            {
                ["name"] = albumTitle ?? title,
                ["album_type"] = albumType,
                ["images"] = new JsonArray(),
            },
            ["duration_ms"] = track.DurationMs is > 0 ? track.DurationMs.Value : 0,
            ["source"] = "musicbrainz",
            ["provider"] = "musicbrainz",
            ["external_ids"] = new JsonObject { ["musicbrainz"] = recordingMbid },
        };
    }

    /// <summary>
    /// Puts a recording on SoulSync's wishlist for a user (again, for a retry) and starts a
    /// wishlist run.
    /// </summary>
    /// <param name="userId">The user asking.</param>
    /// <param name="recordingMbid">The recording, as <see cref="NormalizeMbid"/> gives it.</param>
    /// <param name="hints">What the app knows about it.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How it went.</returns>
    /// <exception cref="HttpRequestException">SoulSync or MusicBrainz couldn't be reached.</exception>
    public async Task<TrackRequestOutcome> RequestAsync(Guid userId, string recordingMbid, TrackRequestHints? hints, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var store = _store() ?? throw new InvalidOperationException("Plugin not initialized");
        var track = await CompleteAsync(recordingMbid, hints ?? new TrackRequestHints(), cancellationToken).ConfigureAwait(false);
        if (track == null)
        {
            return TrackRequestOutcome.Not(TrackRequestOutcomeKind.UnknownRecording, "MusicBrainz has no such recording.");
        }

        var add = await _soulSync.SendAsync(
            HttpMethod.Post,
            "wishlist",
            null,
            new JsonObject
            {
                ["track_data"] = BuildTrackData(recordingMbid, track),
                ["source_type"] = "nori",
                ["failure_reason"] = "Requested in Nori",
            },
            cancellationToken).ConfigureAwait(false);

        if (add.Status == HttpStatusCode.BadRequest)
        {
            return TrackRequestOutcome.Not(TrackRequestOutcomeKind.Refused, RefusalMessage(add.ErrorMessage));
        }

        if (add.Status == HttpStatusCode.Conflict)
        {
            return TrackRequestOutcome.Not(TrackRequestOutcomeKind.NotAdded, add.ErrorMessage ?? "SoulSync didn't add the song.");
        }

        if (!add.IsSuccess)
        {
            throw new HttpRequestException(
                string.Format(CultureInfo.InvariantCulture, "SoulSync answered the wishlist add with HTTP {0} ({1})", (int)add.Status, add.ErrorCode),
                null,
                add.Status);
        }

        var stored = add.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("track", out var row)
            ? SoulSyncClient.ReadString(row, "track_id")
            : null;
        var record = new TrackRequestRecord
        {
            JellyfinUserId = userId,
            RecordingMbid = recordingMbid,
            SoulSyncTrackId = stored ?? recordingMbid,
            Title = track.Title,
            ArtistName = track.Artist,
            AlbumTitle = track.Album,
            ReleaseGroupMbid = NormalizeMbid(track.ReleaseGroupMbid),
            SubmittedAt = store.Get(userId, recordingMbid)?.SubmittedAt ?? now,
            Status = TrackRequestStatus.Queued,
            StatusUpdatedAt = now,
        };
        store.Upsert(record);

        // Other users' requests for it read the same row: start them over too.
        store.UpdateStatus(recordingMbid, TrackRequestStatus.Queued, null, null, null, null, null, now);

        await StartWishlistRunAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Song request: user={UserId} recording={Mbid} wishlist={TrackId} title={Title}",
            userId,
            recordingMbid,
            record.SoulSyncTrackId,
            record.Title);
        return TrackRequestOutcome.Requested(store.Get(userId, recordingMbid) ?? record);
    }

    /// <summary>
    /// Withdraws a request. The song leaves SoulSync's wishlist only when nobody else still
    /// wants it; <paramref name="everyone"/> (an admin's cancel) withdraws every user's.
    /// </summary>
    /// <param name="userId">The user cancelling.</param>
    /// <param name="recordingMbid">The recording.</param>
    /// <param name="everyone">Whether every user's request goes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>False when there was nothing to cancel.</returns>
    public async Task<bool> CancelAsync(Guid userId, string recordingMbid, bool everyone, CancellationToken cancellationToken)
    {
        var store = _store();
        if (store == null)
        {
            return false;
        }

        var all = store.ListByRecording(recordingMbid);
        var mine = all.FirstOrDefault(r => r.JellyfinUserId == userId);
        if (mine == null && !(everyone && all.Count > 0))
        {
            return false;
        }

        var trackId = (mine ?? all[0]).SoulSyncTrackId ?? recordingMbid;
        if (everyone)
        {
            store.DeleteByRecording(recordingMbid);
        }
        else
        {
            store.Delete(userId, recordingMbid);
        }

        if (store.ListByRecording(recordingMbid).Count > 0)
        {
            return true;
        }

        // Best-effort: the request is gone here whatever SoulSync says.
        try
        {
            var removed = await _soulSync.SendAsync(HttpMethod.Delete, "wishlist/" + Uri.EscapeDataString(trackId), null, null, cancellationToken).ConfigureAwait(false);
            if (!removed.IsSuccess && removed.Status != HttpStatusCode.NotFound)
            {
                _logger.LogWarning("SoulSync wishlist removal of {TrackId} answered {Status}", trackId, (int)removed.Status);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Couldn't remove {TrackId} from SoulSync's wishlist; dropped the request anyway", trackId);
        }

        return true;
    }

    private static string RefusalMessage(string? reason) => reason switch
    {
        null or "" => "SoulSync refused the request.",
        "request_limit" => "You've reached your request limit in SoulSync. Try again later.",
        _ => reason,
    };

    // A wishlist run already going (409) picks the song up anyway; any other failure leaves it to
    // SoulSync's own schedule.
    private async Task StartWishlistRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var run = await _soulSync.SendAsync(HttpMethod.Post, "wishlist/process", null, null, cancellationToken).ConfigureAwait(false);
            if (!run.IsSuccess && run.Status != HttpStatusCode.Conflict)
            {
                _logger.LogInformation("SoulSync didn't start a wishlist run ({Status}); its schedule will", (int)run.Status);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogInformation(ex, "Couldn't start a SoulSync wishlist run; its schedule will");
        }
    }

    // The hints, with a missing title or artist read from MusicBrainz. Null when MusicBrainz has
    // no such recording.
    private async Task<TrackRequestHints?> CompleteAsync(string recordingMbid, TrackRequestHints hints, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(hints.Title) && !string.IsNullOrWhiteSpace(hints.Artist))
        {
            return hints;
        }

        if (_musicBrainz == null)
        {
            return null;
        }

        using var response = await _musicBrainz.GetAsync(
            "recording/" + recordingMbid + "?inc=artist-credits+releases+release-groups&fmt=json",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);
        return MergeRecording(hints, doc.RootElement);
    }

    /// <summary>
    /// Fills the hints' gaps from a MusicBrainz recording (<c>recording/{id}?inc=artist-credits+releases+release-groups</c>).
    /// The album is the hinted release group's release when it has one, else its first album.
    /// </summary>
    /// <param name="hints">What the app sent.</param>
    /// <param name="recording">The recording.</param>
    /// <returns>The completed hints, or null when the recording has no title or artist.</returns>
    internal static TrackRequestHints? MergeRecording(TrackRequestHints hints, JsonElement recording)
    {
        var title = string.IsNullOrWhiteSpace(hints.Title) ? SoulSyncClient.ReadString(recording, "title") : hints.Title;
        var artist = hints.Artist;
        if (string.IsNullOrWhiteSpace(artist)
            && recording.TryGetProperty("artist-credit", out var credits)
            && credits.ValueKind == JsonValueKind.Array
            && credits.GetArrayLength() > 0)
        {
            artist = SoulSyncClient.ReadString(credits[0], "name");
        }

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        var merged = new TrackRequestHints
        {
            Title = title,
            Artist = artist,
            Album = hints.Album,
            AlbumType = hints.AlbumType,
            ReleaseGroupMbid = hints.ReleaseGroupMbid,
            DurationMs = hints.DurationMs is > 0 ? hints.DurationMs
                : recording.TryGetProperty("length", out var length) && length.TryGetInt64(out var ms) ? ms : null,
        };

        if (string.IsNullOrWhiteSpace(merged.Album)
            && recording.TryGetProperty("releases", out var releases)
            && releases.ValueKind == JsonValueKind.Array)
        {
            JsonElement? chosen = null;
            foreach (var release in releases.EnumerateArray())
            {
                if (!release.TryGetProperty("release-group", out var group) || group.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (merged.ReleaseGroupMbid != null && string.Equals(SoulSyncClient.ReadString(group, "id"), merged.ReleaseGroupMbid, StringComparison.OrdinalIgnoreCase))
                {
                    chosen = release;
                    break;
                }

                if (chosen == null || (SoulSyncClient.ReadString(group, "primary-type") == "Album"
                    && SoulSyncClient.ReadString(chosen.Value.GetProperty("release-group"), "primary-type") != "Album"))
                {
                    chosen = release;
                }
            }

            if (chosen is { } pick)
            {
                var group = pick.GetProperty("release-group");
                merged.Album = SoulSyncClient.ReadString(pick, "title");
                merged.ReleaseGroupMbid ??= SoulSyncClient.ReadString(group, "id");
                merged.AlbumType ??= SoulSyncClient.ReadString(group, "primary-type");
            }
        }

        return merged;
    }
}

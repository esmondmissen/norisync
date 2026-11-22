using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// Proxies a curated subset of the Lidarr v1 API. The Lidarr API key is held server-side
/// and never exposed to clients — every route requires a valid Jellyfin token, and admin
/// gating is applied per-endpoint where appropriate.
/// </summary>
[ApiController]
[Route("api/norisync/lidarr")]
[Authorize]
public class LidarrController : ControllerBase
{
    private readonly AlbumBackends _backends;
    private readonly RemoteImageFetcher _imageFetcher;
    private readonly MusicBrainzClient _musicBrainz;
    private readonly IMemoryCache _cache;
    private readonly ILogger<LidarrController> _logger;

    /// <summary>
    /// The backend this request talks to: where new album requests go, unless the route acts on
    /// a request already made (<see cref="UseBackendHolding"/>) or on Lidarr's own settings.
    /// </summary>
    private AlbumBackend _backend;

    /// <summary>
    /// Initializes a new instance of the <see cref="LidarrController"/> class.
    /// </summary>
    /// <param name="backends">The album backends (Lidarr, SoulSync's Lidarr-compatible API).</param>
    /// <param name="imageFetcher">Fetcher for external cover-art URLs.</param>
    /// <param name="musicBrainz">MusicBrainz client used for discovery fallbacks.</param>
    /// <param name="cache">Memory cache used to dedupe lookups (covers, discographies).</param>
    /// <param name="logger">Logger.</param>
    public LidarrController(
        AlbumBackends backends,
        RemoteImageFetcher imageFetcher,
        MusicBrainzClient musicBrainz,
        IMemoryCache cache,
        ILogger<LidarrController> logger)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _backends = backends;
        _backend = backends.Active ?? backends.Lidarr;
        _imageFetcher = imageFetcher;
        _musicBrainz = musicBrainz;
        _cache = cache;
        _logger = logger;
    }

    private LidarrClient Upstream => _backend.Client;

    private AlbumSearchScheduler Scheduler => _backend.Scheduler;

    private string BackendName => _backend.Name;

    /// <summary>
    /// Searches Lidarr (and the MusicBrainz catalog it sits in front of) for artists and
    /// albums matching the caller-supplied query. Caller's query string is passed through
    /// verbatim — Lidarr expects <c>?term=...</c>.
    /// </summary>
    /// <returns>The upstream search response.</returns>
    [HttpGet("search")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Search()
        => ForwardJsonAsync(HttpMethod.Get, "search", adminOnly: false);

    /// <summary>
    /// Lists an artist's discography (albums, EPs, singles, etc.), normalized into a
    /// single shape regardless of whether the artist is already in Lidarr. If the artist
    /// is imported we use Lidarr's <c>/album?artistId=...&amp;includeAllArtistAlbums=true</c>;
    /// if not, we fall back to MusicBrainz's <c>/release-group?artist=...</c>. MusicBrainz
    /// responses are cached for 24h to avoid hammering them — discographies change rarely.
    /// Response: <c>{ "source": "library" | "discovery", "albums": [...] }</c>.
    /// </summary>
    /// <param name="mbid">MusicBrainz artist id.</param>
    /// <returns>The normalized album list.</returns>
    [HttpGet("artists/{mbid}/albums")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ArtistAlbums(string mbid)
    {
        if (Upstream.TryGetBaseUri() == null)
        {
            return ServiceUnavailable(BackendName + " is not configured on this server.");
        }

        try
        {
            var artistId = await ResolveArtistIdAsync(mbid, HttpContext.RequestAborted).ConfigureAwait(false);
            if (artistId.HasValue)
            {
                var libraryAlbums = await FetchImportedAlbumsAsync(artistId.Value, HttpContext.RequestAborted).ConfigureAwait(false);
                return Ok(new { source = "library", albums = libraryAlbums });
            }

            var discoveryAlbums = await FetchMusicBrainzAlbumsAsync(mbid, HttpContext.RequestAborted).ConfigureAwait(false);
            if (discoveryAlbums == null)
            {
                return NotFound(new { Error = "Artist not found in MusicBrainz." });
            }

            return Ok(new { source = "discovery", albums = discoveryAlbums });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Upstream unreachable while resolving albums for artist {Mbid}", mbid);
            return ServiceUnavailable("Upstream is unreachable.");
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Album discography for {Mbid} timed out", mbid);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = "Upstream timed out." });
        }
    }

    /// <summary>
    /// Whether Lidarr follows an artist: in Lidarr at all, monitored, and its rule for albums
    /// it discovers later. For the artist page's "Monitor Artist" toggle.
    /// </summary>
    /// <param name="mbid">MusicBrainz artist id.</param>
    /// <returns>The artist's <see cref="ArtistMonitoring"/>.</returns>
    [HttpGet("artists/{mbid}/monitoring")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> ArtistMonitoringState(string mbid)
        => RunArtistMonitoringAsync(mbid, ct => _backend.ArtistMonitoring.GetAsync(mbid, ct));

    /// <summary>
    /// Changes whether Lidarr follows an artist. Fields left out stay as they are. Monitoring an
    /// artist Lidarr hasn't got adds it following only future releases (its existing albums
    /// unmonitored), with the configured root folder and profiles. The artist's albums keep
    /// their own monitored flags either way. Gated by <c>LidarrRequireAdmin</c>.
    /// </summary>
    /// <param name="mbid">MusicBrainz artist id.</param>
    /// <param name="body">The change.</param>
    /// <returns>The artist's <see cref="ArtistMonitoring"/> afterwards.</returns>
    [HttpPut("artists/{mbid}/monitoring")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> SetArtistMonitoring(string mbid, [FromBody] ArtistMonitoringRequest body)
    {
        var config = Plugin.Instance?.Configuration;
        var user = GetJellyfinUser();
        if (user == null || (config?.LidarrRequireAdmin == true && !IsAdministrator(user)))
        {
            return Task.FromResult<IActionResult>(Forbid());
        }

        if (body == null || (body.MonitorNewItems != null && !LidarrArtistMonitoring.IsNewItemRule(body.MonitorNewItems)))
        {
            return Task.FromResult<IActionResult>(BadRequest(new { Error = "MonitorNewItems must be 'all', 'none' or 'new'." }));
        }

        return RunArtistMonitoringAsync(mbid, async ct =>
        {
            var defaults = await _backend.GetAddDefaultsAsync(ct).ConfigureAwait(false);
            var state = await _backend.ArtistMonitoring.SetAsync(mbid, body, defaults, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Lidarr artist monitoring: user={UserId} mbid={Mbid} inLidarr={InLidarr} monitored={Monitored} newItems={NewItems}",
                user.Id,
                mbid,
                state.InLidarr,
                state.Monitored,
                state.MonitorNewItems);
            return state;
        });
    }

    /// <summary>
    /// Lists the tracks on an album, normalized into a single shape regardless of whether
    /// the album is already in Lidarr's library. If the album is imported we use Lidarr's
    /// <c>/track</c> endpoint (which carries <c>hasFile</c>); if not, we fall back to
    /// <c>/album/lookup</c> and extract the tracklist from the MusicBrainz metadata.
    /// Response: <c>{ "source": "library" | "discovery", "tracks": [...] }</c>.
    /// </summary>
    /// <param name="mbid">MusicBrainz release-group id.</param>
    /// <returns>The normalized track list.</returns>
    [HttpGet("albums/{mbid}/tracks")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AlbumTracks(string mbid)
    {
        // Song requests need the tracklist's recording ids even with no album backend: then it
        // comes from MusicBrainz alone.
        var songsOnly = !_backend.IsConfigured && HttpContext.RequestServices.GetRequiredService<SoulSyncClient>().IsConfigured;
        if (Upstream.TryGetBaseUri() == null && !songsOnly)
        {
            return ServiceUnavailable(BackendName + " is not configured on this server.");
        }

        // Library path first.
        int? albumId = null;
        try
        {
            if (_backend.ListsAlbumTracks && !songsOnly)
            {
                albumId = await ResolveAlbumIdAsync(mbid, HttpContext.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lidarr unreachable while resolving album id for {Mbid}", mbid);
            return ServiceUnavailable(BackendName + " is unreachable.");
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Lidarr album-id lookup for {Mbid} timed out", mbid);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = BackendName + " timed out." });
        }

        if (albumId.HasValue)
        {
            try
            {
                var libraryTracks = await FetchImportedTracksAsync(albumId.Value, HttpContext.RequestAborted).ConfigureAwait(false);
                return Ok(new { source = "library", tracks = libraryTracks });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Lidarr unreachable while fetching tracks for album id {AlbumId}", albumId.Value);
                return ServiceUnavailable(BackendName + " is unreachable.");
            }
            catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Lidarr track fetch for album id {AlbumId} timed out", albumId.Value);
                return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = BackendName + " timed out." });
            }
        }

        // Discovery path: MusicBrainz direct (Jellyfin server must have outbound to musicbrainz.org).
        try
        {
            var discoveryTracks = await FetchMusicBrainzTracksAsync(mbid, HttpContext.RequestAborted).ConfigureAwait(false);
            if (discoveryTracks == null)
            {
                return NotFound(new { Error = "Album not found in MusicBrainz." });
            }

            return Ok(new { source = "discovery", tracks = discoveryTracks });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MusicBrainz unreachable while resolving tracks for release-group {Mbid}", mbid);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { Error = "MusicBrainz is unreachable from the Jellyfin server. Check outbound network access to musicbrainz.org.", Upstream = "musicbrainz" });
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "MusicBrainz track fetch for release-group {Mbid} timed out", mbid);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = "MusicBrainz timed out.", Upstream = "musicbrainz" });
        }
    }

    /// <summary>
    /// Submits a request for an album. Looks up the album metadata via Lidarr, merges in
    /// the configured root folder / quality profile / metadata profile defaults, and POSTs
    /// to Lidarr's <c>/album</c> endpoint. Inserts a tracking row in the requests store on
    /// success so the caller can see the request's progress via <see cref="ListRequests"/>.
    /// Gated by <c>LidarrRequireAdmin</c>.
    /// </summary>
    /// <param name="mbid">MusicBrainz release-group id.</param>
    /// <returns>A minimal summary of the submitted request.</returns>
    [HttpPost("albums/{mbid}/request")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RequestAlbum(string mbid)
    {
        if (Upstream.TryGetBaseUri() == null)
        {
            return ServiceUnavailable(BackendName + " is not configured on this server.");
        }

        var config = Plugin.Instance?.Configuration;
        var store = _backend.Store;
        if (config == null || store == null)
        {
            return ServiceUnavailable("Plugin not initialized.");
        }

        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        if (config.LidarrRequireAdmin && !IsAdministrator(user))
        {
            return Forbid();
        }

        try
        {
            var defaults = await _backend.GetAddDefaultsAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            if (!defaults.IsComplete)
            {
                return _backend.Kind == RequestBackend.SoulSync
                    ? ServiceUnavailable("SoulSync's root folder and profiles couldn't be read.")
                    : BadRequest(new { Error = "Lidarr defaults (root folder, quality profile, metadata profile) are not configured." });
            }

            // 1. Lookup the album metadata Lidarr/MusicBrainz knows about.
            JsonNode? lookupRoot;
            using (var lookupResp = await Upstream.SendAsync(
                HttpMethod.Get,
                "album/lookup",
                $"?term=lidarr:{Uri.EscapeDataString(mbid)}",
                body: null,
                HttpCompletionOption.ResponseContentRead,
                HttpContext.RequestAborted).ConfigureAwait(false))
            {
                if (!lookupResp.IsSuccessStatusCode)
                {
                    return ServiceUnavailable(BackendName + " lookup failed.");
                }

                var lookupBytes = await lookupResp.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                lookupRoot = JsonNode.Parse(lookupBytes);
            }

            if (lookupRoot is not JsonArray lookupArr || lookupArr.Count == 0 || lookupArr[0] is null)
            {
                return NotFound(new { Error = "Album not found in MusicBrainz." });
            }

            // 2. Build the POST body from the lookup result + config defaults.
            var album = lookupArr[0]!.DeepClone().AsObject();
            album["monitored"] = true;
            album["anyReleaseOk"] = true;
            album["qualityProfileId"] = defaults.QualityProfileId;
            album["metadataProfileId"] = defaults.MetadataProfileId;
            // The plugin searches itself (AlbumSearchScheduler): Lidarr's searchForNewAlbum
            // never runs for a new artist and fires a stray search a day later instead.
            album["addOptions"] = new JsonObject { ["searchForNewAlbum"] = false };

            if (album["artist"] is JsonObject artistObj)
            {
                // Only read when the artist is new (an existing artist keeps its own settings).
                // Monitor just this album: "all" with a real metadata profile monitored the
                // whole discography, and RSS then downloaded every album of it.
                artistObj["qualityProfileId"] = defaults.QualityProfileId;
                artistObj["metadataProfileId"] = defaults.MetadataProfileId;
                artistObj["rootFolderPath"] = defaults.RootFolderPath;
                artistObj["monitored"] = true;
                artistObj["monitorNewItems"] = "none";
                artistObj["tags"] ??= new JsonArray();
                artistObj["addOptions"] = BuildNewArtistAddOptions(mbid);
            }

            // 3. POST to Lidarr.
            var postContent = new ByteArrayContent(Encoding.UTF8.GetBytes(album.ToJsonString()));
            postContent.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);

            using var addResp = await Upstream.SendAsync(
                HttpMethod.Post,
                "album",
                queryString: null,
                postContent,
                HttpCompletionOption.ResponseContentRead,
                HttpContext.RequestAborted).ConfigureAwait(false);

            var addBytes = await addResp.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted).ConfigureAwait(false);

            if (!addResp.IsSuccessStatusCode)
            {
                // Lidarr rejects re-adds of an existing album with a 400 carrying
                // {"errorMessage":"This album has already been added", ...}. Treat that as
                // a user-driven re-request: look up the existing album, ensure it's
                // monitored, track it in the requests store, and queue a fresh search so
                // Lidarr retries indexers (the previous attempt may have hit nothing
                // grabable). Falls through to the normal error forward if anything in this
                // recovery path itself fails.
                if (addResp.StatusCode == HttpStatusCode.BadRequest && IsAlreadyAddedError(addBytes))
                {
                    var existing = await FetchAlbumByMbidAsync(mbid, HttpContext.RequestAborted).ConfigureAwait(false);
                    var existingId = (int?)existing?["id"] ?? 0;
                    if (existing != null && existingId > 0)
                    {
                        // Monitored so it reads as wanted in Lidarr. The artist is left as the
                        // user set it: AlbumSearch doesn't check monitoring, and the schedule
                        // stands in for the RSS an unmonitored artist misses.
                        await EnsureAlbumMonitoredAsync(existingId, HttpContext.RequestAborted).ConfigureAwait(false);

                        var existingTitle = (string?)existing["title"];
                        var existingArtistName = (string?)existing["artist"]?["artistName"];
                        var existingNow = DateTimeOffset.UtcNow;
                        var existingRecord = new RequestRecord
                        {
                            JellyfinUserId = user.Id,
                            LidarrAlbumId = existingId,
                            AlbumMbid = mbid,
                            AlbumTitle = existingTitle,
                            ArtistName = existingArtistName,
                            SubmittedAt = existingNow,
                            Status = RequestStatus.Pending,
                            StatusUpdatedAt = existingNow
                        };
                        store.Upsert(existingRecord);

                        _logger.LogInformation(
                            "Lidarr re-request (album already imported): user={UserId} albumId={AlbumId} mbid={Mbid} title={Title}",
                            user.Id,
                            existingId,
                            mbid,
                            existingTitle);

                        await Scheduler.RestartAsync(store, AlbumSearchScheduler.Fresh(existingId, existingNow), HttpContext.RequestAborted).ConfigureAwait(false);
                        await SearchNowIfReadyAsync(store, existingId, HttpContext.RequestAborted).ConfigureAwait(false);

                        return Ok(FindRecord(store, user.Id, existingId) ?? existingRecord);
                    }
                }

                Response.StatusCode = (int)addResp.StatusCode;
                return new FileContentResult(addBytes, MediaTypeNames.Application.Json);
            }

            // 4. Parse Lidarr's response to grab the new album id + display fields, then insert our row.
            var addedNode = JsonNode.Parse(addBytes);
            var addedAlbumId = (int?)addedNode?["id"] ?? 0;
            if (addedAlbumId == 0)
            {
                _logger.LogWarning("Lidarr POST /album returned 2xx but no id; body: {Body}", Encoding.UTF8.GetString(addBytes));
                return ServiceUnavailable(BackendName + " returned an unexpected response.");
            }

            var title = (string?)addedNode?["title"];
            var artistName = (string?)addedNode?["artist"]?["artistName"];

            var now = DateTimeOffset.UtcNow;
            var record = new RequestRecord
            {
                JellyfinUserId = user.Id,
                LidarrAlbumId = addedAlbumId,
                AlbumMbid = mbid,
                AlbumTitle = title,
                ArtistName = artistName,
                SubmittedAt = now,
                Status = RequestStatus.Pending,
                StatusUpdatedAt = now
            };
            store.Upsert(record);

            _logger.LogInformation(
                "Lidarr request submitted: user={UserId} albumId={AlbumId} mbid={Mbid} title={Title}",
                user.Id,
                addedAlbumId,
                mbid,
                title);

            // No search here: the album's releases load in a refresh that runs after this
            // POST, and a search before then rejects every sized result. The schedule
            // searches once they're in (the request sync, every minute).
            await Scheduler.RestartAsync(store, AlbumSearchScheduler.Fresh(addedAlbumId, now), HttpContext.RequestAborted).ConfigureAwait(false);
            if (_backend.Kind == RequestBackend.SoulSync)
            {
                // SoulSync reads the tracklist itself when it searches: nothing to wait for.
                await SearchNowIfReadyAsync(store, addedAlbumId, HttpContext.RequestAborted).ConfigureAwait(false);
            }

            return Ok(FindRecord(store, user.Id, addedAlbumId) ?? record);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lidarr unreachable while requesting album {Mbid}", mbid);
            return ServiceUnavailable(BackendName + " is unreachable.");
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Lidarr request for album {Mbid} timed out", mbid);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = BackendName + " timed out." });
        }
    }

    /// <summary>
    /// Runs an interactive (manual) search for an album: asks Lidarr to query its configured
    /// indexers and returns the raw list of release candidates unchanged (Lidarr's
    /// <c>GET /release?albumId=...</c>), as <see cref="ReleaseCandidate"/>s (no download URLs).
    /// The album must already exist in Lidarr — interactive
    /// search operates on Lidarr's internal album id, which discovery-only albums don't have
    /// yet; callers get a <c>409</c> with <c>Code="album_not_in_lidarr"</c> in that case.
    /// Gated by <c>LidarrRequireAdmin</c>, since each call loads the indexers and exposes
    /// download URLs.
    /// </summary>
    /// <param name="mbid">MusicBrainz release-group id.</param>
    /// <returns>The candidates, in Lidarr's ranking.</returns>
    [HttpGet("albums/{mbid}/releases")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AlbumReleases(string mbid)
    {
        UseBackendOfCallersRequest(mbid);
        if (!_backend.SupportsInteractiveSearch && _backend.IsConfigured)
        {
            return InteractiveSearchUnsupported();
        }

        if (Upstream.TryGetBaseUri() == null)
        {
            return ServiceUnavailable(BackendName + " is not configured on this server.");
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return ServiceUnavailable("Plugin not initialized.");
        }

        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        if (config.LidarrRequireAdmin && !IsAdministrator(user))
        {
            return Forbid();
        }

        try
        {
            var albumId = await ResolveAlbumIdAsync(mbid, HttpContext.RequestAborted).ConfigureAwait(false);
            if (albumId == null)
            {
                return Conflict(new
                {
                    Error = "This album is not in " + BackendName + " yet. Add it before running an interactive search.",
                    Code = "album_not_in_lidarr"
                });
            }

            using var upstream = await Upstream.SendAsync(
                HttpMethod.Get,
                "release",
                "?albumId=" + albumId.Value.ToString(CultureInfo.InvariantCulture),
                body: null,
                HttpCompletionOption.ResponseContentRead,
                HttpContext.RequestAborted,
                LidarrClient.InteractiveSearchTimeout).ConfigureAwait(false);

            if (!upstream.IsSuccessStatusCode)
            {
                _logger.LogWarning("Lidarr interactive search for album {AlbumId} returned {Status}", albumId.Value, (int)upstream.StatusCode);
                return ServiceUnavailable(BackendName + "'s search failed.");
            }

            var bytes = await upstream.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            return Ok(ReleaseCandidate.FromLidarr(doc.RootElement));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lidarr unreachable while resolving album id for interactive search of {Mbid}", mbid);
            return ServiceUnavailable(BackendName + " is unreachable.");
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Interactive search for {Mbid} timed out", mbid);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = BackendName + " timed out." });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Lidarr answered the interactive search for {Mbid} with something that isn't JSON", mbid);
            return ServiceUnavailable(BackendName + " returned an unexpected response.");
        }
    }

    /// <summary>
    /// Grabs one specific release candidate (from <see cref="AlbumReleases"/>) and hands it to
    /// Lidarr's download client (Lidarr <c>POST /release</c>), bypassing quality-profile
    /// auto-selection. The album must already exist in Lidarr. The album and its artist are
    /// flipped to monitored — mirroring the request flow — so Lidarr imports the result and the
    /// album reads as wanted; then a tracking row is upserted so the download surfaces in the
    /// caller's <see cref="ListRequests"/> and <see cref="Queue"/>. Gated by
    /// <c>LidarrRequireAdmin</c>.
    /// </summary>
    /// <param name="mbid">MusicBrainz release-group id.</param>
    /// <param name="body">The release to grab (<c>guid</c> + <c>indexerId</c> from a candidate).</param>
    /// <returns>The tracking row for the grabbed album (same shape as <see cref="ListRequests"/>).</returns>
    [HttpPost("albums/{mbid}/releases/grab")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GrabRelease(string mbid, [FromBody] GrabReleaseRequest body)
    {
        UseBackendOfCallersRequest(mbid);
        if (!_backend.SupportsInteractiveSearch && _backend.IsConfigured)
        {
            return InteractiveSearchUnsupported();
        }

        if (Upstream.TryGetBaseUri() == null)
        {
            return ServiceUnavailable(BackendName + " is not configured on this server.");
        }

        var config = Plugin.Instance?.Configuration;
        var store = _backend.Store;
        if (config == null || store == null)
        {
            return ServiceUnavailable("Plugin not initialized.");
        }

        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        if (config.LidarrRequireAdmin && !IsAdministrator(user))
        {
            return Forbid();
        }

        if (body == null || string.IsNullOrWhiteSpace(body.Guid) || body.IndexerId <= 0)
        {
            return BadRequest(new { Error = "Both 'guid' and 'indexerId' are required." });
        }

        try
        {
            // The album must already be in Lidarr — grab operates on its internal id, and we
            // need the album metadata (title/artist) for the tracking row.
            var existing = await FetchAlbumByMbidAsync(mbid, HttpContext.RequestAborted).ConfigureAwait(false);
            var existingId = (int?)existing?["id"] ?? 0;
            if (existing == null || existingId <= 0)
            {
                return Conflict(new
                {
                    Error = "This album is not in " + BackendName + " yet. Add it before grabbing a release.",
                    Code = "album_not_in_lidarr"
                });
            }

            // As a request: the album reads as wanted; the artist keeps the user's setting.
            // Best-effort; a failure doesn't block the grab.
            await EnsureAlbumMonitoredAsync(existingId, HttpContext.RequestAborted).ConfigureAwait(false);

            // Grab the specific release. Lidarr resolves the cached RemoteAlbum from guid+indexerId
            // (populated by the preceding interactive search) and sends it to the download client.
            var grabBody = BuildGrabBody(body.Guid, body.IndexerId, existingId, (int?)existing["artist"]?["id"] ?? (int?)existing["artistId"]);
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(grabBody.ToJsonString()));
            content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);

            using var grabResp = await Upstream.SendAsync(
                HttpMethod.Post,
                "release",
                queryString: null,
                content,
                HttpCompletionOption.ResponseContentRead,
                HttpContext.RequestAborted).ConfigureAwait(false);

            var grabBytes = await grabResp.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted).ConfigureAwait(false);

            if (grabResp.StatusCode == HttpStatusCode.NotFound)
            {
                // Lidarr keeps a search's results for 30 minutes; after that it can't find the
                // guid. Not a 404 to the client, which reads that as a plugin without the route.
                return StatusCode(StatusCodes.Status410Gone, new
                {
                    Error = "These search results have expired. Search again.",
                    Code = "release_expired"
                });
            }

            if (!grabResp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Lidarr grab failed for album {AlbumId} (mbid={Mbid}) indexer={IndexerId}: {Status}",
                    existingId,
                    mbid,
                    body.IndexerId,
                    (int)grabResp.StatusCode);

                Response.StatusCode = (int)grabResp.StatusCode;
                return new FileContentResult(grabBytes, MediaTypeNames.Application.Json);
            }

            var now = DateTimeOffset.UtcNow;
            var record = new RequestRecord
            {
                JellyfinUserId = user.Id,
                LidarrAlbumId = existingId,
                AlbumMbid = mbid,
                AlbumTitle = (string?)existing["title"],
                ArtistName = (string?)existing["artist"]?["artistName"],
                SubmittedAt = now,
                Status = RequestStatus.Pending,
                StatusUpdatedAt = now
            };
            store.Upsert(record);
            await Scheduler.RestartAsync(store, AlbumSearchScheduler.AfterGrab(existingId, now), HttpContext.RequestAborted).ConfigureAwait(false);

            _logger.LogInformation(
                "Lidarr interactive grab: user={UserId} albumId={AlbumId} mbid={Mbid} indexer={IndexerId} title={Title}",
                user.Id,
                existingId,
                mbid,
                body.IndexerId,
                record.AlbumTitle);

            return Ok(FindRecord(store, user.Id, existingId) ?? record);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lidarr unreachable while grabbing release for album {Mbid}", mbid);
            return ServiceUnavailable(BackendName + " is unreachable.");
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Lidarr grab for album {Mbid} timed out", mbid);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = BackendName + " timed out." });
        }
    }

    /// <summary>
    /// "Search Again": starts the album's search schedule over (a fresh retry window) and
    /// searches at once if Lidarr has its releases; otherwise the request sync searches as soon
    /// as they load. The album must be in Lidarr (<c>409</c>, <c>album_not_in_lidarr</c>). Tracks
    /// the album for the caller if they had no request for it. Gated by <c>LidarrRequireAdmin</c>.
    /// </summary>
    /// <param name="mbid">MusicBrainz release-group id.</param>
    /// <returns>The caller's tracking row for the album, search fields included.</returns>
    [HttpPost("albums/{mbid}/search")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SearchAlbum(string mbid)
    {
        UseBackendOfCallersRequest(mbid);
        if (Upstream.TryGetBaseUri() == null)
        {
            return ServiceUnavailable(BackendName + " is not configured on this server.");
        }

        var config = Plugin.Instance?.Configuration;
        var store = _backend.Store;
        if (config == null || store == null)
        {
            return ServiceUnavailable("Plugin not initialized.");
        }

        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        if (config.LidarrRequireAdmin && !IsAdministrator(user))
        {
            return Forbid();
        }

        try
        {
            var existing = await FetchAlbumByMbidAsync(mbid, HttpContext.RequestAborted).ConfigureAwait(false);
            var albumId = (int?)existing?["id"] ?? 0;
            if (existing == null || albumId <= 0)
            {
                return Conflict(new
                {
                    Error = "This album is not in " + BackendName + " yet. Request it first.",
                    Code = "album_not_in_lidarr"
                });
            }

            await EnsureAlbumMonitoredAsync(albumId, HttpContext.RequestAborted).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            if (FindRecord(store, user.Id, albumId) == null)
            {
                store.Upsert(new RequestRecord
                {
                    JellyfinUserId = user.Id,
                    LidarrAlbumId = albumId,
                    AlbumMbid = mbid,
                    AlbumTitle = (string?)existing["title"],
                    ArtistName = (string?)existing["artist"]?["artistName"],
                    SubmittedAt = now,
                    Status = RequestStatus.Pending,
                    StatusUpdatedAt = now
                });
            }

            await Scheduler.RestartAsync(store, AlbumSearchScheduler.Fresh(albumId, now), HttpContext.RequestAborted).ConfigureAwait(false);
            await SearchNowIfReadyAsync(store, albumId, HttpContext.RequestAborted).ConfigureAwait(false);

            _logger.LogInformation("Lidarr search again: user={UserId} albumId={AlbumId} mbid={Mbid}", user.Id, albumId, mbid);
            return Ok(FindRecord(store, user.Id, albumId));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lidarr unreachable while searching again for album {Mbid}", mbid);
            return ServiceUnavailable(BackendName + " is unreachable.");
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Search again for album {Mbid} timed out", mbid);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = BackendName + " timed out." });
        }
    }

    /// <summary>
    /// Lists the caller's tracked Lidarr requests. Administrators may pass <c>?all=true</c>
    /// to see every user's requests, or <c>?userId=&lt;guid&gt;</c> to filter to a specific user.
    /// </summary>
    /// <param name="userId">Optional Jellyfin user id (admin only).</param>
    /// <param name="all">When true and the caller is an admin, returns rows for every user.</param>
    /// <returns>The matching request rows.</returns>
    [HttpGet("requests")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult ListRequests([FromQuery] Guid? userId, [FromQuery] bool all = false)
    {
        var stores = _backends.All.Select(b => b.Store).OfType<RequestsStore>().ToList();
        if (stores.Count == 0)
        {
            return Ok(Array.Empty<RequestRecord>());
        }

        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        var isAdmin = IsAdministrator(user);

        if (all)
        {
            if (!isAdmin)
            {
                return Forbid();
            }

            return Ok(stores.SelectMany(s => s.ListAll()).ToList());
        }

        if (userId.HasValue && userId.Value != user.Id)
        {
            if (!isAdmin)
            {
                return Forbid();
            }

            return Ok(stores.SelectMany(s => s.ListByUser(userId.Value)).ToList());
        }

        return Ok(stores.SelectMany(s => s.ListByUser(user.Id)).ToList());
    }

    /// <summary>
    /// Returns Lidarr's download queue. For non-admin callers, the queue is filtered to
    /// entries whose <c>albumId</c> appears in the caller's tracked requests; admins see the
    /// full upstream response unchanged.
    /// </summary>
    /// <returns>The filtered queue response.</returns>
    [HttpGet("queue")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Queue()
    {
        if (Upstream.TryGetBaseUri() == null)
        {
            return ServiceUnavailable(BackendName + " is not configured on this server.");
        }

        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        var isAdmin = IsAdministrator(user);
        HashSet<int>? allowedAlbumIds = null;
        if (!isAdmin)
        {
            var store = _backend.Store;
            allowedAlbumIds = store?.GetAlbumIdsForUser(user.Id) ?? new HashSet<int>();
            if (allowedAlbumIds.Count == 0)
            {
                return Ok(new
                {
                    page = 1,
                    pageSize = 0,
                    totalRecords = 0,
                    records = Array.Empty<object>()
                });
            }
        }

        try
        {
            using var upstream = await Upstream.SendAsync(
                HttpMethod.Get,
                "queue",
                Request.QueryString.HasValue ? Request.QueryString.Value : null,
                body: null,
                HttpCompletionOption.ResponseContentRead,
                HttpContext.RequestAborted).ConfigureAwait(false);

            var bytes = await upstream.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            var contentType = upstream.Content.Headers.ContentType?.ToString() ?? MediaTypeNames.Application.Json;

            if (!upstream.IsSuccessStatusCode || allowedAlbumIds == null)
            {
                Response.StatusCode = (int)upstream.StatusCode;
                return new FileContentResult(bytes, contentType);
            }

            // Non-admin path: filter records[] to caller's tracked albumIds.
            var node = JsonNode.Parse(bytes);
            if (node is JsonObject obj && obj["records"] is JsonArray records)
            {
                var kept = new JsonArray();
                foreach (var rec in records)
                {
                    if (rec is JsonObject recObj
                        && recObj["albumId"] is JsonValue albumIdVal
                        && albumIdVal.TryGetValue<int>(out var albumId)
                        && allowedAlbumIds.Contains(albumId))
                    {
                        kept.Add(rec.DeepClone());
                    }
                }

                obj["records"] = kept;
                obj["totalRecords"] = kept.Count;
            }

            var filteredBytes = Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "{}");
            return new FileContentResult(filteredBytes, MediaTypeNames.Application.Json);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lidarr queue fetch failed");
            return ServiceUnavailable(BackendName + " is unreachable.");
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Lidarr queue fetch timed out");
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = BackendName + " timed out." });
        }
    }

    /// <summary>
    /// Cancels a tracked request: unmonitors the album in Lidarr and removes the matching
    /// row(s) from the requests store. Non-admins can only cancel their own rows.
    /// </summary>
    /// <param name="lidarrAlbumId">The album id on its backend.</param>
    /// <param name="backend">The backend holding the request (the record's <c>Backend</c>). An
    /// older app sends none: the store holding the id for the caller is used, the active one
    /// first.</param>
    /// <returns>204 on success, 404 if the caller has no row for this album.</returns>
    [HttpDelete("requests/{lidarrAlbumId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CancelRequest(int lidarrAlbumId, [FromQuery] RequestBackend? backend = null)
    {
        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        if (backend is { } named)
        {
            _backend = _backends.For(named);
        }
        else if (_backend.Store?.GetAlbumIdsForUser(user.Id).Contains(lidarrAlbumId) != true)
        {
            UseBackendHolding(user.Id, mbid: null, lidarrAlbumId);
        }

        if (Upstream.TryGetBaseUri() == null)
        {
            return ServiceUnavailable(BackendName + " is not configured on this server.");
        }

        var store = _backend.Store;
        if (store == null)
        {
            return NotFound();
        }

        var isAdmin = IsAdministrator(user);
        if (!isAdmin && !store.GetAlbumIdsForUser(user.Id).Contains(lidarrAlbumId))
        {
            return NotFound();
        }

        // Best-effort unmonitor in Lidarr. We still drop the local row even if Lidarr is
        // unreachable — the user expressed intent to cancel; the sync task will reconcile
        // if Lidarr resurfaces the album later.
        try
        {
            var monitorBody = new JsonObject
            {
                ["albumIds"] = new JsonArray(lidarrAlbumId),
                ["monitored"] = false
            };
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(monitorBody.ToJsonString()));
            content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);

            using var resp = await Upstream.SendAsync(
                HttpMethod.Put,
                "album/monitor",
                queryString: null,
                content,
                HttpCompletionOption.ResponseContentRead,
                HttpContext.RequestAborted).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Lidarr unmonitor for album {AlbumId} returned {Status}", lidarrAlbumId, (int)resp.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to unmonitor album {AlbumId} in Lidarr; dropping local row anyway", lidarrAlbumId);
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Lidarr unmonitor for album {AlbumId} timed out; dropping local row anyway", lidarrAlbumId);
        }

        if (isAdmin)
        {
            store.DeleteByAlbumId(lidarrAlbumId);
        }
        else
        {
            store.Delete(user.Id, lidarrAlbumId);
        }

        return NoContent();
    }

    /// <summary>
    /// Fetches artist cover art (poster) for a MusicBrainz artist id and streams it back.
    /// Resolves the image URL via Lidarr's lookup endpoint, then fetches the binary from
    /// the external host (typically fanart.tv). The mbid → URL mapping is cached for an
    /// hour so repeat renders skip the lookup hop.
    /// </summary>
    /// <param name="mbid">MusicBrainz artist id.</param>
    /// <returns>The cover image.</returns>
    [HttpGet("covers/artist/{mbid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> CoverArtist(string mbid)
        => CoverAsync("artist", mbid, "poster");

    /// <summary>
    /// Fetches release-group cover art for a MusicBrainz release-group id and streams it
    /// back. Resolution is identical to <see cref="CoverArtist"/> but uses the album lookup
    /// endpoint and picks the <c>cover</c> coverType.
    /// </summary>
    /// <param name="rgid">MusicBrainz release-group id.</param>
    /// <returns>The cover image.</returns>
    [HttpGet("covers/release-group/{rgid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> CoverReleaseGroup(string rgid)
        => CoverAsync("album", rgid, "cover");

    /// <summary>
    /// Lists Lidarr's configured root folders. Used by the plugin configuration UI to
    /// populate the root-folder dropdown.
    /// </summary>
    /// <returns>The upstream root-folder list.</returns>
    [HttpGet("rootfolders")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> RootFolders()
    {
        _backend = _backends.Lidarr;
        return ForwardJsonAsync(HttpMethod.Get, "rootfolder", adminOnly: true);
    }

    /// <summary>
    /// Lists Lidarr's quality profiles. Used by the plugin configuration UI to populate the
    /// quality-profile dropdown.
    /// </summary>
    /// <returns>The upstream quality-profile list.</returns>
    [HttpGet("qualityprofiles")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> QualityProfiles()
    {
        _backend = _backends.Lidarr;
        return ForwardJsonAsync(HttpMethod.Get, "qualityprofile", adminOnly: true);
    }

    /// <summary>
    /// Lists Lidarr's metadata profiles. Used by the plugin configuration UI to populate the
    /// metadata-profile dropdown.
    /// </summary>
    /// <returns>The upstream metadata-profile list.</returns>
    [HttpGet("metadataprofiles")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> MetadataProfiles()
    {
        _backend = _backends.Lidarr;
        return ForwardJsonAsync(HttpMethod.Get, "metadataprofile", adminOnly: true);
    }

    /// <summary>
    /// Tries a Lidarr URL and API key from the request body, without saving them, and returns
    /// the root-folder and profile lists. Lets the configuration form fill its dropdowns while
    /// the admin is still typing. Admin-only: it makes the server call the given URL.
    /// </summary>
    /// <param name="request">The URL and key to try.</param>
    /// <returns>The probe outcome; connection failures are a 200 with a status, not an error.</returns>
    [HttpPost("probe")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<LidarrProbeResponse>> Probe([FromBody] LidarrProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = GetJellyfinUser();
        if (user == null || !IsAdministrator(user))
        {
            return Forbid();
        }

        return await Upstream.ProbeAsync(request.BaseUrl, request.ApiKey, HttpContext.RequestAborted).ConfigureAwait(false);
    }

    private async Task<IActionResult> ForwardJsonAsync(
        HttpMethod method,
        string upstreamPath,
        bool adminOnly,
        string? queryStringOverride = null)
    {
        if (Upstream.TryGetBaseUri() == null)
        {
            return ServiceUnavailable(BackendName + " is not configured on this server.");
        }

        if (adminOnly)
        {
            var user = GetJellyfinUser();
            if (user == null || !IsAdministrator(user))
            {
                return Forbid();
            }
        }

        var queryString = queryStringOverride
            ?? (Request.QueryString.HasValue ? Request.QueryString.Value : null);

        try
        {
            using var upstream = await Upstream.SendAsync(
                method,
                upstreamPath,
                queryString,
                body: null,
                HttpCompletionOption.ResponseContentRead,
                HttpContext.RequestAborted).ConfigureAwait(false);

            var bytes = await upstream.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            var contentType = upstream.Content.Headers.ContentType?.ToString() ?? MediaTypeNames.Application.Json;

            Response.StatusCode = (int)upstream.StatusCode;
            return new FileContentResult(bytes, contentType);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lidarr upstream call to {Path} failed", upstreamPath);
            return ServiceUnavailable(BackendName + " is unreachable.");
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Lidarr upstream call to {Path} timed out", upstreamPath);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = BackendName + " timed out." });
        }
    }

    private async Task<IActionResult> CoverAsync(string lookupPath, string mbid, string preferredCoverType)
    {
        // Lidarr's lookups carry fanart.tv images; SoulSync's compat API may have none for artists.
        if (_backends.Lidarr.IsConfigured)
        {
            _backend = _backends.Lidarr;
        }

        if (Upstream.TryGetBaseUri() == null)
        {
            return ServiceUnavailable(BackendName + " is not configured on this server.");
        }

        string? remoteUrl;
        try
        {
            remoteUrl = await ResolveCoverUrlAsync(lookupPath, mbid, preferredCoverType, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lidarr lookup for {Path}/{Mbid} failed", lookupPath, mbid);
            return ServiceUnavailable(BackendName + " is unreachable.");
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Lidarr lookup for {Path}/{Mbid} timed out", lookupPath, mbid);
            return StatusCode(StatusCodes.Status504GatewayTimeout);
        }

        if (remoteUrl == null)
        {
            return NotFound();
        }

        HttpResponseMessage? upstream = null;
        try
        {
            upstream = await _imageFetcher.GetAsync(remoteUrl, HttpContext.RequestAborted).ConfigureAwait(false);

            if (!upstream.IsSuccessStatusCode)
            {
                var status = (int)upstream.StatusCode;
                upstream.Dispose();
                return StatusCode(status);
            }

            var contentType = upstream.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var stream = await upstream.Content.ReadAsStreamAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            var wrapped = new UpstreamDisposingStream(stream, upstream);
            upstream = null; // ownership transferred to the wrapper
            return new FileStreamResult(wrapped, contentType);
        }
        catch (HttpRequestException ex)
        {
            upstream?.Dispose();
            _logger.LogWarning(ex, "Remote cover fetch from {Url} failed", remoteUrl);
            return StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            upstream?.Dispose();
            throw;
        }
        catch (TaskCanceledException ex)
        {
            upstream?.Dispose();
            _logger.LogWarning(ex, "Remote cover fetch from {Url} timed out", remoteUrl);
            return StatusCode(StatusCodes.Status504GatewayTimeout);
        }
    }

    // Lidarr's "album already added" rejection is shaped as a FluentValidation error array:
    //   [{ "propertyName": "ForeignAlbumId", "errorMessage": "This album has already been added", ... }]
    // We match on the message substring rather than the property because the propertyName
    // has flipped between releases.
    private static bool IsAlreadyAddedError(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var err in doc.RootElement.EnumerateArray())
            {
                if (err.ValueKind != JsonValueKind.Object
                    || !err.TryGetProperty("errorMessage", out var msgProp)
                    || msgProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var msg = msgProp.GetString();
                if (msg != null && msg.Contains("already been added", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Body isn't Lidarr's validation-error shape — let the caller forward as-is.
        }

        return false;
    }

    // GET /album?foreignAlbumId=<mbid> returns the existing album (or an empty array).
    private async Task<JsonObject?> FetchAlbumByMbidAsync(string mbid, CancellationToken cancellationToken)
    {
        using var upstream = await Upstream.SendAsync(
            HttpMethod.Get,
            "album",
            $"?foreignAlbumId={Uri.EscapeDataString(mbid)}",
            body: null,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (!upstream.IsSuccessStatusCode)
        {
            return null;
        }

        var bytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return null;
        }

        return JsonNode.Parse(bytes) is JsonArray arr && arr.Count > 0
            ? arr[0]?.AsObject()
            : null;
    }

    /// <summary>
    /// The <c>addOptions</c> for an artist created by a request: only the requested album
    /// monitored, and no artist-wide search (the plugin searches the one album itself).
    /// </summary>
    /// <param name="releaseGroupMbid">The requested album's release-group id.</param>
    /// <returns>The add options.</returns>
    internal static JsonObject BuildNewArtistAddOptions(string releaseGroupMbid) => new()
    {
        ["monitor"] = "all",
        ["albumsToMonitor"] = new JsonArray(releaseGroupMbid),
        ["searchForMissingAlbums"] = false
    };

    private async Task<IActionResult> RunArtistMonitoringAsync(string mbid, Func<CancellationToken, Task<ArtistMonitoring>> run)
    {
        if (Upstream.TryGetBaseUri() == null)
        {
            return ServiceUnavailable(BackendName + " is not configured on this server.");
        }

        // Lidarr binds `mbId` as a GUID: anything else is a 400 there, read as "refused".
        if (!Guid.TryParse(mbid, out _))
        {
            return BadRequest(new { Error = "An artist MusicBrainz id is required." });
        }

        try
        {
            return Ok(await run(HttpContext.RequestAborted).ConfigureAwait(false));
        }
        catch (LidarrUpstreamException ex) when (ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Lidarr refused an artist monitoring change for {Mbid}: {Status} {Body}", mbid, (int)ex.Status, ex.Body);
            return StatusCode((int)ex.Status, new { Error = ex.Status == HttpStatusCode.NotFound ? "Artist not found in MusicBrainz." : BackendName + " refused the change." });
        }
        catch (LidarrUpstreamException ex)
        {
            _logger.LogWarning("Lidarr artist monitoring for {Mbid} failed: {Status} {Body}", mbid, (int)ex.Status, ex.Body);
            return ServiceUnavailable(BackendName + " answered with an error.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lidarr unreachable for artist monitoring of {Mbid}", mbid);
            return ServiceUnavailable(BackendName + " is unreachable.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Lidarr answered artist monitoring for {Mbid} with something that isn't JSON", mbid);
            return ServiceUnavailable(BackendName + " returned an unexpected response.");
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Lidarr artist monitoring for {Mbid} timed out", mbid);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = BackendName + " timed out." });
        }
    }

    /// <summary>
    /// Lidarr's <c>POST release</c> body. The album and artist ids let Lidarr grab a result it
    /// couldn't match to an album by its title (<c>downloadAllowed: false</c>).
    /// </summary>
    /// <param name="guid">The result's guid.</param>
    /// <param name="indexerId">The result's indexer id.</param>
    /// <param name="albumId">The Lidarr album id.</param>
    /// <param name="artistId">The Lidarr artist id, when known.</param>
    /// <returns>The body.</returns>
    internal static JsonObject BuildGrabBody(string guid, int indexerId, int albumId, int? artistId)
    {
        var body = new JsonObject
        {
            ["guid"] = guid,
            ["indexerId"] = indexerId,
            ["albumId"] = albumId
        };
        if (artistId is int id && id > 0)
        {
            body["artistId"] = id;
        }

        return body;
    }

    private static RequestRecord? FindRecord(RequestsStore store, Guid userId, int albumId)
        => store.ListByUser(userId).FirstOrDefault(r => r.LidarrAlbumId == albumId);

    // Moves the album's schedule on right away, so a ready album is searched now rather than
    // at the next sweep. Best-effort: any failure leaves it to the sweep.
    private async Task SearchNowIfReadyAsync(RequestsStore store, int albumId, CancellationToken cancellationToken)
    {
        try
        {
            using var upstream = await Upstream.SendAsync(
                HttpMethod.Get,
                "album",
                "?albumIds=" + albumId.ToString(CultureInfo.InvariantCulture),
                body: null,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);
            if (!upstream.IsSuccessStatusCode)
            {
                return;
            }

            var bytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return;
            }

            await Scheduler.AdvanceStoredAsync(
                store,
                albumId,
                doc.RootElement[0],
                DateTimeOffset.UtcNow,
                maySearch: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Immediate search for album {AlbumId} failed; the request sync will retry", albumId);
        }
    }

    // Best-effort: flip the album to monitored. An unmonitored album won't be picked up
    // by AlbumSearch, so this is required before re-running the search on a re-request.
    private async Task EnsureAlbumMonitoredAsync(int albumId, CancellationToken cancellationToken)
    {
        try
        {
            var body = new JsonObject
            {
                ["albumIds"] = new JsonArray(albumId),
                ["monitored"] = true
            };
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()));
            content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);

            using var resp = await Upstream.SendAsync(
                HttpMethod.Put,
                "album/monitor",
                queryString: null,
                content,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Lidarr monitor=true for album {AlbumId} returned {Status}",
                    albumId,
                    (int)resp.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to re-monitor album {AlbumId}", albumId);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Monitor request for album {AlbumId} timed out", albumId);
        }
    }

    private async Task<string?> ResolveCoverUrlAsync(string lookupPath, string mbid, string preferredCoverType, CancellationToken cancellationToken)
    {
        var cacheKey = $"norisync:lidarr:cover:{lookupPath}:{mbid}:{preferredCoverType}";
        if (_cache.TryGetValue<string>(cacheKey, out var cached) && cached != null)
        {
            return cached;
        }

        using var upstream = await Upstream.SendAsync(
            HttpMethod.Get,
            $"{lookupPath}/lookup",
            $"?term=lidarr:{Uri.EscapeDataString(mbid)}",
            body: null,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (!upstream.IsSuccessStatusCode)
        {
            return null;
        }

        var bytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        if (!doc.RootElement[0].TryGetProperty("images", out var images)
            || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? exactMatch = null;
        string? firstAny = null;
        foreach (var img in images.EnumerateArray())
        {
            var url = img.TryGetProperty("remoteUrl", out var remoteProp) ? remoteProp.GetString() : null;
            if (string.IsNullOrEmpty(url) && img.TryGetProperty("url", out var urlProp))
            {
                url = urlProp.GetString();
            }

            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            firstAny ??= url;

            var coverType = img.TryGetProperty("coverType", out var coverProp) ? coverProp.GetString() : null;
            if (string.Equals(coverType, preferredCoverType, StringComparison.OrdinalIgnoreCase))
            {
                exactMatch = url;
                break;
            }
        }

        var chosen = exactMatch ?? firstAny;
        if (chosen != null)
        {
            _cache.Set(cacheKey, chosen, TimeSpan.FromMinutes(60));
        }

        return chosen;
    }

    // Returns the Lidarr numeric artist id for the given MusicBrainz id, or null if the
    // artist has not been imported into Lidarr.
    private async Task<int?> ResolveArtistIdAsync(string mbid, CancellationToken cancellationToken)
        => await ResolveFirstIdAsync("artist", "mbId", mbid, cancellationToken).ConfigureAwait(false);

    // Returns the Lidarr numeric album id for the given MusicBrainz release-group id, or
    // null if the album has not been imported into Lidarr.
    private async Task<int?> ResolveAlbumIdAsync(string mbid, CancellationToken cancellationToken)
        => await ResolveFirstIdAsync("album", "foreignAlbumId", mbid, cancellationToken).ConfigureAwait(false);

    private async Task<int?> ResolveFirstIdAsync(string upstreamPath, string queryKey, string mbid, CancellationToken cancellationToken)
    {
        using var upstream = await Upstream.SendAsync(
            HttpMethod.Get,
            upstreamPath,
            $"?{queryKey}={Uri.EscapeDataString(mbid)}",
            body: null,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (!upstream.IsSuccessStatusCode)
        {
            return null;
        }

        var bytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var first = doc.RootElement[0];
        if (first.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
        {
            return id;
        }

        return null;
    }

    private async Task<JsonArray> FetchImportedTracksAsync(int albumId, CancellationToken cancellationToken)
    {
        var tracks = new JsonArray();

        using var upstream = await Upstream.SendAsync(
            HttpMethod.Get,
            "track",
            $"?albumId={albumId}",
            body: null,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (!upstream.IsSuccessStatusCode)
        {
            return tracks;
        }

        var bytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return tracks;
        }

        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return tracks;
        }

        foreach (var t in doc.RootElement.EnumerateArray())
        {
            tracks.Add(NormalizeTrack(t, fallbackDiscNumber: null, hasFile: ReadHasFile(t)));
        }

        return tracks;
    }

    // Returns null when MusicBrainz responds 404 (release-group unknown). Throws
    // HttpRequestException on transient failures so the controller can surface a 503.
    // Lidarr's own /album/lookup is intentionally NOT used here — it only contains tracks
    // when the metadata server has been asked to import the album, which it usually
    // hasn't for unimported artists. MusicBrainz is the canonical source and always works.
    private async Task<JsonArray?> FetchMusicBrainzTracksAsync(string releaseGroupMbid, CancellationToken cancellationToken)
    {
        var cacheKey = $"norisync:mb:rg-tracks:{releaseGroupMbid}";
        if (_cache.TryGetValue<JsonArray>(cacheKey, out var cached) && cached != null)
        {
            return cached;
        }

        // Browse pattern: list releases that belong to this release-group, with their media
        // and recording info. The lookup form `/release-group/<rgid>?inc=recordings` returns
        // 400 because `recordings` is only a valid inc value on /release, not /release-group.
        var path = $"release?release-group={Uri.EscapeDataString(releaseGroupMbid)}&inc=recordings+media&fmt=json";
        using var upstream = await _musicBrainz.GetAsync(path, cancellationToken).ConfigureAwait(false);

        if (upstream.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!upstream.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"MusicBrainz returned {(int)upstream.StatusCode}");
        }

        var bytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return new JsonArray();
        }

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        if (!root.TryGetProperty("releases", out var releases)
            || releases.ValueKind != JsonValueKind.Array
            || releases.GetArrayLength() == 0)
        {
            return new JsonArray();
        }

        // Walk releases in order, taking the first one that actually has tracks. MB
        // sometimes surfaces "placeholder" releases without media; skip those.
        var tracks = new JsonArray();
        foreach (var release in releases.EnumerateArray())
        {
            if (!release.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var releaseTracks = new JsonArray();
            foreach (var medium in media.EnumerateArray())
            {
                int? mediumNumber = ReadInt(medium, "position");

                if (medium.TryGetProperty("tracks", out var mTracks) && mTracks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in mTracks.EnumerateArray())
                    {
                        releaseTracks.Add(NormalizeMusicBrainzTrack(t, mediumNumber));
                    }
                }
            }

            if (releaseTracks.Count > 0)
            {
                tracks = releaseTracks;
                break;
            }
        }

        _cache.Set(cacheKey, tracks, TimeSpan.FromHours(24));
        return tracks;
    }

    private static JsonObject NormalizeMusicBrainzTrack(JsonElement t, int? discNumber)
    {
        var title = ReadString(t, "title");
        long? duration = ReadLong(t, "length");
        string? recordingMbid = null;

        // Track-level title/length aren't always populated; fall back to the linked recording.
        if (t.TryGetProperty("recording", out var rec) && rec.ValueKind == JsonValueKind.Object)
        {
            title ??= ReadString(rec, "title");
            duration ??= ReadLong(rec, "length");
            recordingMbid = ReadString(rec, "id");
        }

        return new JsonObject
        {
            ["title"] = title,
            ["recordingMbid"] = recordingMbid,
            ["trackNumber"] = ReadInt(t, "position") ?? ReadInt(t, "number"),
            ["discNumber"] = discNumber,
            ["duration"] = duration,
            ["hasFile"] = false
        };
    }

    private static JsonObject NormalizeTrack(JsonElement t, int? fallbackDiscNumber, bool hasFile)
    {
        return new JsonObject
        {
            ["title"] = ReadString(t, "title"),
            ["recordingMbid"] = ReadString(t, "foreignRecordingId"),
            ["trackNumber"] = ReadInt(t, "absoluteTrackNumber") ?? ReadInt(t, "trackNumber"),
            ["discNumber"] = ReadInt(t, "mediumNumber") ?? fallbackDiscNumber,
            ["duration"] = ReadLong(t, "duration"),
            ["hasFile"] = hasFile
        };
    }

    private async Task<JsonArray> FetchImportedAlbumsAsync(int artistId, CancellationToken cancellationToken)
    {
        var albums = new JsonArray();

        using var upstream = await Upstream.SendAsync(
            HttpMethod.Get,
            "album",
            $"?artistId={artistId}&includeAllArtistAlbums=true",
            body: null,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);

        if (!upstream.IsSuccessStatusCode)
        {
            return albums;
        }

        var bytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return albums;
        }

        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return albums;
        }

        foreach (var a in doc.RootElement.EnumerateArray())
        {
            albums.Add(NormalizeLibraryAlbum(a));
        }

        return albums;
    }

    // Returns null when MusicBrainz responds 404 (artist unknown). Throws HttpRequestException
    // for transient failures so the controller can return 503 instead of a misleading 404.
    private async Task<JsonArray?> FetchMusicBrainzAlbumsAsync(string artistMbid, CancellationToken cancellationToken)
    {
        var cacheKey = $"norisync:mb:rg:{artistMbid}";
        if (_cache.TryGetValue<JsonArray>(cacheKey, out var cached) && cached != null)
        {
            return cached;
        }

        var path = $"release-group?artist={Uri.EscapeDataString(artistMbid)}&limit=100&fmt=json";
        using var upstream = await _musicBrainz.GetAsync(path, cancellationToken).ConfigureAwait(false);

        if (upstream.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!upstream.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"MusicBrainz returned {(int)upstream.StatusCode}");
        }

        var bytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return new JsonArray();
        }

        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("release-groups", out var rgs) || rgs.ValueKind != JsonValueKind.Array)
        {
            return new JsonArray();
        }

        var albums = new JsonArray();
        foreach (var rg in rgs.EnumerateArray())
        {
            albums.Add(NormalizeMusicBrainzAlbum(rg));
        }

        _cache.Set(cacheKey, albums, TimeSpan.FromHours(24));
        return albums;
    }

    private static JsonObject NormalizeLibraryAlbum(JsonElement a)
    {
        return new JsonObject
        {
            ["mbid"] = ReadString(a, "foreignAlbumId"),
            ["title"] = ReadString(a, "title"),
            ["albumType"] = ReadString(a, "albumType"),
            ["secondaryTypes"] = ReadStringArray(a, "secondaryTypes"),
            ["releaseYear"] = ExtractYear(ReadString(a, "releaseDate")),
            ["inLibrary"] = true
        };
    }

    private static JsonObject NormalizeMusicBrainzAlbum(JsonElement rg)
    {
        return new JsonObject
        {
            ["mbid"] = ReadString(rg, "id"),
            ["title"] = ReadString(rg, "title"),
            ["albumType"] = ReadString(rg, "primary-type"),
            ["secondaryTypes"] = ReadStringArray(rg, "secondary-types"),
            ["releaseYear"] = ExtractYear(ReadString(rg, "first-release-date")),
            ["inLibrary"] = false
        };
    }

    private static JsonArray? ReadStringArray(JsonElement e, string property)
    {
        if (!e.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new JsonArray();
        foreach (var v in arr.EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.String)
            {
                result.Add(v.GetString());
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static int? ExtractYear(string? date)
    {
        if (string.IsNullOrEmpty(date) || date.Length < 4)
        {
            return null;
        }

        return int.TryParse(date[..4], out var year) ? year : null;
    }

    private static bool ReadHasFile(JsonElement t)
        => t.TryGetProperty("hasFile", out var hf) && hf.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? ReadInt(JsonElement e, string property)
    {
        if (!e.TryGetProperty(property, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
        {
            return i;
        }

        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s))
        {
            return s;
        }

        return null;
    }

    private static long? ReadLong(JsonElement e, string property)
        => e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)
            ? l
            : null;

    // Routes acting on a request already made talk to the backend that holds it: switching the
    // album backend leaves earlier requests where they went.
    private void UseBackendOfCallersRequest(string mbid)
    {
        if (GetJellyfinUser() is { } user)
        {
            UseBackendHolding(user.Id, mbid);
        }
    }

    private ObjectResult InteractiveSearchUnsupported()
        => StatusCode(StatusCodes.Status501NotImplemented, new
        {
            Error = BackendName + " doesn't list releases to pick from. Its own search runs instead.",
            Code = "interactive_search_unsupported"
        });

    private void UseBackendHolding(Guid userId, string? mbid, int? albumId = null)
    {
        foreach (var backend in _backends.All)
        {
            var rows = backend.Store?.ListByUser(userId);
            if (rows != null && rows.Any(r => (mbid != null && string.Equals(r.AlbumMbid, mbid, StringComparison.OrdinalIgnoreCase))
                || (albumId != null && r.LidarrAlbumId == albumId)))
            {
                _backend = backend;
                return;
            }
        }
    }

    private ObjectResult ServiceUnavailable(string message)
        => StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = message });

    private static bool IsAdministrator(User user) => RequestCallers.IsAdministrator(user);

    private User? GetJellyfinUser() => RequestCallers.Resolve(this);

    /// <summary>
    /// Wraps the upstream cover stream so disposing the response stream also disposes the
    /// underlying <see cref="HttpResponseMessage"/>. Without this the response would be
    /// returned to the pool while we're still streaming bytes out of it.
    /// </summary>
    private sealed class UpstreamDisposingStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;

        public UpstreamDisposingStream(Stream inner, HttpResponseMessage response)
        {
            _inner = inner;
            _response = response;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

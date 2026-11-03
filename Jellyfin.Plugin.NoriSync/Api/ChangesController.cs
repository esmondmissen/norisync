using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MessagePack;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// API controller for querying tracked library changes and initial sync.
/// </summary>
[ApiController]
[Route("api/norisync")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize]
public class ChangesController : ControllerBase
{
    /// <summary>
    /// Manifest GUID of the third-party AudioMuse AI Jellyfin plugin (NeptuneHub/audiomuse-ai-plugin).
    /// </summary>
    private static readonly Guid AudioMusePluginId = new("e3831be1-c025-4ebc-bc79-121ad0dfc4e1");

    /// <summary>
    /// Gets a logger. Resolved per use from request services to match this controller's existing
    /// service-location idiom rather than introducing constructor injection.
    /// </summary>
    private ILogger Logger
        => HttpContext.RequestServices.GetRequiredService<ILogger<ChangesController>>();

    /// <summary>
    /// Gets albums that have changes or contain tracks with changes, in the same format as the albums endpoint.
    /// </summary>
    /// <param name="since">Only return changes after this UTC timestamp (ISO 8601).</param>
    /// <param name="until">Optional inclusive upper bound on the change timestamp (ISO 8601). Clients should fetch this value once from <c>/timestamp</c> at the start of a paginated sync session and reuse it across pages so new writes during paging don't leak into the result. Future timestamps are clamped to the current server time.</param>
    /// <param name="startIndex">The index of the first album to return (default 0).</param>
    /// <param name="limit">Maximum number of albums to return (default 50, max 200).</param>
    /// <param name="libraryIds">Library ids to restrict results to albums/tracks in those libraries. Required; repeat the query parameter for multiple values.</param>
    /// <returns>A paginated response of changed albums with nested tracks and artist details.</returns>
    [HttpGet("changes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<SyncAlbumsResponse> GetChanges(
        [FromQuery] string? since = null,
        [FromQuery] string? until = null,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50,
        [FromQuery] Guid[]? libraryIds = null)
    {
        var store = Plugin.Instance?.Store;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        TouchInstance();

        var librarySet = NormalizeLibraryIds(libraryIds);
        if (librarySet == null)
        {
            return MissingLibraryIds("/changes");
        }

        DateTimeOffset? sinceDate = null;
        if (!string.IsNullOrEmpty(since) && DateTimeOffset.TryParse(since, out var parsed))
        {
            sinceDate = parsed;
        }

        DateTimeOffset? untilDate = null;
        if (!string.IsNullOrEmpty(until) && DateTimeOffset.TryParse(until, out var parsedUntil))
        {
            untilDate = SyncClock.ClampUntil(parsedUntil);
        }

        var minSafeSince = store.GetMinSafeSince();
        if (sinceDate.HasValue && minSafeSince.HasValue && sinceDate.Value < minSafeSince.Value)
        {
            Logger.LogWarning(
                "NoriSync {Path} cursor {Since} predates the retention watermark {MinSafeSince}; returning 410 so the client re-runs a full sync",
                HttpContext.Request.Path,
                sinceDate.Value,
                minSafeSince.Value);

            return StatusCode(StatusCodes.Status410Gone, new
            {
                Error = "cursor_expired",
                MinSafeSince = minSafeSince.Value,
                Message = "The provided 'since' cursor is older than the server's retention watermark. Discard local cursor and perform a full sync via /albums and /playlists."
            });
        }

        // Artist records are read alongside albums and tracks so that artist deletions and
        // renames reach clients at all. An artist otherwise only ever arrives as a side effect
        // of an album that references it, which by definition never happens once it is gone.
        var syncedTypes = new[] { TrackedItemType.Album, TrackedItemType.Track, TrackedItemType.Artist };
        var changesResponse = store.GetChanges(sinceDate, untilDate, syncedTypes);

        var libraryManager = HttpContext.RequestServices.GetRequiredService<ILibraryManager>();
        var userDataManager = HttpContext.RequestServices.GetRequiredService<IUserDataManager>();
        var imageProcessor = HttpContext.RequestServices.GetRequiredService<IImageProcessor>();

        var user = GetJellyfinUser();

        var libraryError = ValidateLibrariesAccess(librarySet, user, libraryManager);
        if (libraryError != null)
        {
            return libraryError;
        }

        var artistLookup = BuildArtistLookup(libraryManager);
        var referencedArtists = new Dictionary<Guid, MusicArtist>();

        // Separate deletions from adds/modifies. For album-level changes we return the
        // album's full track list (safety net for event gaps and album re-matches). For
        // track-only changes we return just the flagged tracks and mark the payload as
        // partial so the client merges rather than replaces.
        var fullAlbumIds = new HashSet<Guid>();
        var changedTracksByAlbum = new Dictionary<Guid, HashSet<Guid>>();
        // Tombstones list only items that are still gone. Ids are derived from names and
        // paths, so a rescan can delete an item and re-create it under the same id; a
        // client applying that tombstone would drop a live item.
        var deletedAlbumIds = new HashSet<Guid>();
        var deletedTrackIds = new HashSet<Guid>();
        var deletedArtistIds = new HashSet<Guid>();

        foreach (var change in changesResponse.Changes)
        {
            if (change.ItemType == TrackedItemType.Album)
            {
                if (change.ChangeType == ChangeType.Deleted)
                {
                    if (libraryManager.GetItemById(change.ItemId) is not MusicAlbum)
                    {
                        deletedAlbumIds.Add(change.ItemId);
                    }
                }
                else
                {
                    fullAlbumIds.Add(change.ItemId);
                }
            }
            else if (change.ItemType == TrackedItemType.Track)
            {
                if (change.ChangeType == ChangeType.Deleted)
                {
                    if (libraryManager.GetItemById(change.ItemId) is not Audio)
                    {
                        deletedTrackIds.Add(change.ItemId);
                    }
                }
                else
                {
                    var trackItem = libraryManager.GetItemById(change.ItemId);
                    if (trackItem != null && (user == null || trackItem.IsVisible(user)))
                    {
                        var parentId = trackItem.ParentId;
                        if (!changedTracksByAlbum.TryGetValue(parentId, out var set))
                        {
                            set = new HashSet<Guid>();
                            changedTracksByAlbum[parentId] = set;
                        }

                        set.Add(change.ItemId);
                    }
                }
            }
            else if (change.ItemType == TrackedItemType.Artist)
            {
                if (change.ChangeType == ChangeType.Deleted)
                {
                    if (libraryManager.GetItemById(change.ItemId) is not MusicArtist)
                    {
                        deletedArtistIds.Add(change.ItemId);
                    }
                }
                else if (libraryManager.GetItemById(change.ItemId) is MusicArtist changedArtist)
                {
                    // Carries a rename or metadata edit even when no album of theirs changed.
                    referencedArtists.TryAdd(changedArtist.Id, changedArtist);
                }
            }
        }

        var albumIds = new HashSet<Guid>(fullAlbumIds);
        foreach (var id in changedTracksByAlbum.Keys)
        {
            albumIds.Add(id);
        }

        // Look up album items and sort by name
        var albumItems = new List<MusicAlbum>();
        foreach (var albumId in albumIds)
        {
            var item = libraryManager.GetItemById(albumId);
            if (item is MusicAlbum album
                && (user == null || album.IsVisible(user))
                && (librarySet == null || IsItemInAnyLibrary(album, librarySet, libraryManager)))
            {
                albumItems.Add(album);
            }
        }

        albumItems.Sort((a, b) => string.Compare(a.SortName, b.SortName, StringComparison.Ordinal));
        var totalCount = albumItems.Count;

        // Apply pagination
        limit = Math.Clamp(limit, 1, 200);
        startIndex = Math.Max(startIndex, 0);
        var pagedItems = albumItems.Skip(startIndex).Take(limit).ToList();

        // Build DTOs for the paged subset
        var albums = new List<SyncAlbumDto>();
        foreach (var album in pagedItems)
        {
            var isFullAlbum = fullAlbumIds.Contains(album.Id);
            HashSet<Guid>? trackFilter = null;
            if (!isFullAlbum && changedTracksByAlbum.TryGetValue(album.Id, out var changedTracks))
            {
                trackFilter = changedTracks;
            }

            var albumPrimary = album.GetImageInfo(MediaBrowser.Model.Entities.ImageType.Primary, 0);

            var tracks = GetTracks(album, user, libraryManager, userDataManager, imageProcessor, albumPrimary, artistLookup, referencedArtists, trackFilter);

            // A partial album with no visible tracks after filtering carries no useful
            // information — skip it rather than emitting an empty delta.
            if (!isFullAlbum && tracks.Count == 0)
            {
                continue;
            }

            albums.Add(new SyncAlbumDto
            {
                Id = album.Id,
                Name = album.Name ?? string.Empty,
                SortName = album.SortName ?? string.Empty,
                ReleaseYear = album.ProductionYear,
                Description = album.Overview,
                AlbumArtistIds = ResolveArtistIds(album.AlbumArtists, artistLookup, referencedArtists, libraryManager),
                Genres = SplitGenres(album.Genres),
                Tracks = tracks,
                DateAdded = album.DateCreated,
                TracksArePartial = !isFullAlbum,
                MusicBrainzReleaseGroupId = GetMusicBrainzReleaseGroupId(album)
            });
        }

        var artists = referencedArtists.Values.Select(a => new SyncArtistDto
        {
            Id = a.Id,
            Name = a.Name ?? string.Empty,
            SortName = a.SortName ?? string.Empty,
            Description = a.Overview,
            MusicBrainzId = GetMusicBrainzArtistId(a)
        }).ToList();

        var response = new SyncAlbumsResponse
        {
            TotalCount = totalCount,
            StartIndex = startIndex,
            Albums = albums,
            Artists = artists,
            DeletedAlbumIds = deletedAlbumIds.ToList(),
            DeletedTrackIds = deletedTrackIds.ToList(),
            DeletedArtistIds = deletedArtistIds.ToList()
        };

        if (Request.Headers.Accept.Any(h => h != null && h.Contains("application/x-msgpack", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = MessagePackSerializer.Serialize(response);
            return File(bytes, "application/x-msgpack");
        }

        return Ok(response);
    }

    /// <summary>
    /// Gets the current server UTC timestamp. Use this to avoid clock-sync issues when polling for changes.
    /// </summary>
    /// <returns>The current server timestamp.</returns>
    [HttpGet("timestamp")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetTimestamp()
    {
        return Ok(new
        {
            Timestamp = SyncClock.IssueCursor(),
            MinSafeSince = Plugin.Instance?.Store?.GetMinSafeSince()
        });
    }

    /// <summary>
    /// Gets plugin identification and version information. Allows clients to detect whether the
    /// plugin is installed on a Jellyfin server without authentication.
    /// </summary>
    /// <returns>The plugin id, name, and version.</returns>
    [HttpGet("info")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult GetInfo()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var lidarrClient = HttpContext.RequestServices.GetRequiredService<Services.LidarrClient>();
        var lidarrConfigured = lidarrClient.TryGetBaseUri() != null;
        if (lidarrConfigured && (plugin.Configuration?.LidarrRequireAdmin ?? true))
        {
            var user = GetJellyfinUser();
            lidarrConfigured = user?.Permissions
                .FirstOrDefault(p => p.Kind == PermissionKind.IsAdministrator)?.Value ?? false;
        }

        // Detect whether the third-party AudioMuse AI plugin is installed so clients can
        // surface its sonic-analysis features. Match on the plugin's manifest GUID, which is
        // its stable identifier across releases.
        var pluginManager = HttpContext.RequestServices.GetRequiredService<MediaBrowser.Common.Plugins.IPluginManager>();
        var audioMuse = pluginManager.Plugins.FirstOrDefault(p => p.Id == AudioMusePluginId);

        return Ok(new
        {
            plugin.Id,
            plugin.Name,
            Version = plugin.Version.ToString(),
            LidarrConfigured = lidarrConfigured,
            AudioMuseInstalled = audioMuse != null,
            AudioMuseEnabled = audioMuse?.IsEnabledAndSupported ?? false,
            AudioMuseVersion = audioMuse?.Version.ToString()
        });
    }

    /// <summary>
    /// Gets a summary of change tracking status.
    /// </summary>
    /// <returns>Status information including total tracked changes and latest timestamp.</returns>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult GetStatus()
    {
        var store = Plugin.Instance?.Store;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var allChanges = store.GetChanges(null, null, null);
        return Ok(new
        {
            TotalTrackedChanges = allChanges.TotalCount,
            allChanges.LatestTimestamp,
            MinSafeSince = store.GetMinSafeSince(),
            IsActive = true
        });
    }

    /// <summary>
    /// Gets the calling user's NoriSync-relevant settings, derived from their Jellyfin
    /// permissions. Currently exposes the download permission; new fields will be added
    /// over time.
    /// </summary>
    /// <returns>The user's settings.</returns>
    [HttpGet("user/settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult GetUserSettings()
    {
        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        var allowDownloads = user.Permissions
            .FirstOrDefault(p => p.Kind == PermissionKind.EnableContentDownloading)?.Value ?? false;

        return Ok(new
        {
            AllowDownloads = allowDownloads
        });
    }

    /// <summary>
    /// Clears all tracked change records. Requires administrator privileges.
    /// </summary>
    /// <returns>No content on success.</returns>
    [HttpPost("reset")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult Reset()
    {
        var store = Plugin.Instance?.Store;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        store.Clear();
        return NoContent();
    }

    /// <summary>
    /// Gets a paginated list of albums with their tracks for initial sync.
    /// </summary>
    /// <param name="startIndex">The index of the first album to return (default 0).</param>
    /// <param name="limit">Maximum number of albums to return (default 50, max 200).</param>
    /// <param name="libraryIds">Library ids to restrict results to albums in those libraries. Required; repeat the query parameter for multiple values.</param>
    /// <returns>A paginated response of albums with nested tracks and artist details.</returns>
    [HttpGet("albums")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<SyncAlbumsResponse> GetAlbums(
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50,
        [FromQuery] Guid[]? libraryIds = null)
    {
        TouchInstance();

        var librarySet = NormalizeLibraryIds(libraryIds);
        if (librarySet == null)
        {
            return MissingLibraryIds("/albums");
        }

        var libraryManager = HttpContext.RequestServices.GetRequiredService<ILibraryManager>();
        var userDataManager = HttpContext.RequestServices.GetRequiredService<IUserDataManager>();
        var imageProcessor = HttpContext.RequestServices.GetRequiredService<IImageProcessor>();

        limit = Math.Clamp(limit, 1, 200);
        startIndex = Math.Max(startIndex, 0);

        var user = GetJellyfinUser();

        var libraryError = ValidateLibrariesAccess(librarySet, user, libraryManager);
        if (libraryError != null)
        {
            return libraryError;
        }

        var artistLookup = BuildArtistLookup(libraryManager);
        var referencedArtists = new Dictionary<Guid, MusicArtist>();

        var scopedAlbumIds = GetScopedAlbumIds(user, librarySet, libraryManager);
        var totalCount = scopedAlbumIds.Count;

        var albums = new List<SyncAlbumDto>();
        foreach (var albumId in scopedAlbumIds.Skip(startIndex).Take(limit))
        {
            // Only the id list is memoised; every page resolves its albums fresh, so metadata
            // edits are never served stale. An album deleted inside the memo window resolves to
            // null and is skipped — the page is one short until the memo expires, which is
            // self-correcting and far cheaper than invalidating on every library event.
            if (libraryManager.GetItemById(albumId) is not MusicAlbum album)
            {
                continue;
            }

            var albumPrimary = album.GetImageInfo(MediaBrowser.Model.Entities.ImageType.Primary, 0);

            albums.Add(new SyncAlbumDto
            {
                Id = album.Id,
                Name = album.Name ?? string.Empty,
                SortName = album.SortName ?? string.Empty,
                ReleaseYear = album.ProductionYear,
                Description = album.Overview,
                AlbumArtistIds = ResolveArtistIds(album.AlbumArtists, artistLookup, referencedArtists, libraryManager),
                Genres = SplitGenres(album.Genres),
                Tracks = GetTracks(album, user, libraryManager, userDataManager, imageProcessor, albumPrimary, artistLookup, referencedArtists),
                DateAdded = album.DateCreated,
                TracksArePartial = false,
                MusicBrainzReleaseGroupId = GetMusicBrainzReleaseGroupId(album)
            });
        }

        var artists = referencedArtists.Values.Select(a => new SyncArtistDto
        {
            Id = a.Id,
            Name = a.Name ?? string.Empty,
            SortName = a.SortName ?? string.Empty,
            Description = a.Overview,
            MusicBrainzId = GetMusicBrainzArtistId(a)
        }).ToList();

        if (totalCount == 0)
        {
            LogEmptySync(librarySet, libraryManager);
        }

        var response = new SyncAlbumsResponse
        {
            TotalCount = totalCount,
            StartIndex = startIndex,
            Albums = albums,
            Artists = artists
        };

        if (Request.Headers.Accept.Any(h => h != null && h.Contains("application/x-msgpack", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = MessagePackSerializer.Serialize(response);
            return File(bytes, "application/x-msgpack");
        }

        return Ok(response);
    }

    /// <summary>
    /// Gets a paginated list of audio playlists with their track IDs for initial sync.
    /// </summary>
    /// <param name="startIndex">The index of the first playlist to return (default 0).</param>
    /// <param name="limit">Maximum number of playlists to return (default 50, max 200).</param>
    /// <param name="libraryIds">Optional. When present, each playlist's <c>Tracks</c> is restricted to audio items reachable through these libraries, so the feed only names tracks the caller's <c>/albums</c> walk (scoped to the same ids) will deliver. Repeat the query parameter for multiple values. Omitted or empty means unscoped — the pre-existing behaviour, kept for older clients.</param>
    /// <returns>A paginated response of audio playlists with track IDs.</returns>
    [HttpGet("playlists")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SyncPlaylistsResponse> GetPlaylists(
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50,
        [FromQuery] Guid[]? libraryIds = null)
    {
        TouchInstance();

        var libraryManager = HttpContext.RequestServices.GetRequiredService<ILibraryManager>();
        var userManager = HttpContext.RequestServices.GetRequiredService<IUserManager>();

        limit = Math.Clamp(limit, 1, 200);
        startIndex = Math.Max(startIndex, 0);

        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        // Optional here, unlike /albums and /changes: playlists are not library-owned, so an
        // unscoped request is meaningful (and is what pre-scoping clients send). When a scope
        // IS given it gets the same access validation as the album feeds, so a bad id fails
        // the same way on every endpoint rather than silently returning empty playlists.
        var librarySet = NormalizeLibraryIds(libraryIds);
        if (librarySet != null)
        {
            var libraryError = ValidateLibrariesAccess(librarySet, user, libraryManager);
            if (libraryError != null)
            {
                return libraryError;
            }
        }

        // Enumerate all playlists and filter by per-user visibility rather than relying on the
        // query's User scope. The User-scoped query does not surface playlists owned by another
        // user but shared with (or made public to) this caller; Playlist.IsVisible does — it is
        // the same check the web UI and the /playlists/changes feed use. Pagination is applied
        // in-memory after filtering so TotalCount reflects the true visible count.
        var allPlaylists = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
        });

        var ownerNameCache = new Dictionary<Guid, string>();

        var visiblePlaylists = new List<SyncPlaylistDto>();
        foreach (var item in allPlaylists)
        {
            if (item is not Playlist playlist
                || playlist.MediaType != MediaType.Audio
                || !playlist.IsVisible(user))
            {
                continue;
            }

            visiblePlaylists.Add(BuildPlaylistDto(playlist, user, userManager, ownerNameCache, librarySet, libraryManager));
        }

        var pagedPlaylists = visiblePlaylists.Skip(startIndex).Take(limit).ToList();

        var response = new SyncPlaylistsResponse
        {
            TotalCount = visiblePlaylists.Count,
            StartIndex = startIndex,
            Playlists = pagedPlaylists
        };

        if (Request.Headers.Accept.Any(h => h != null && h.Contains("application/x-msgpack", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = MessagePackSerializer.Serialize(response);
            return File(bytes, "application/x-msgpack");
        }

        return Ok(response);
    }

    /// <summary>
    /// Gets audio playlists that changed since the given cursor, for incremental sync. Each
    /// changed playlist visible to the caller is returned in full; playlists that were deleted
    /// or are no longer visible to the caller (e.g. unshared or made private) are returned as
    /// tombstones in <c>DeletedPlaylistIds</c> so the client can drop its local copy.
    /// </summary>
    /// <param name="since">Only return changes after this UTC timestamp (ISO 8601). Omit for a full catch-up.</param>
    /// <param name="until">Optional inclusive upper bound on the change timestamp (ISO 8601). Fetch once from <c>/timestamp</c> and reuse across pages so writes during paging don't leak in. Future timestamps are clamped to the current server time.</param>
    /// <param name="startIndex">The index of the first changed playlist to return (default 0).</param>
    /// <param name="limit">Maximum number of changed playlists to return (default 50, max 200). Does not bound <c>DeletedPlaylistIds</c>.</param>
    /// <param name="libraryIds">Optional. Same entry scoping as <c>/playlists</c>: restricts each playlist's <c>Tracks</c> to audio items reachable through these libraries. Repeat for multiple values; omitted or empty means unscoped. Does not affect which playlists are returned or tombstoned.</param>
    /// <returns>A paginated response of changed playlists with tombstones for the caller.</returns>
    [HttpGet("playlists/changes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PlaylistChangesResponse> GetPlaylistChanges(
        [FromQuery] string? since = null,
        [FromQuery] string? until = null,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50,
        [FromQuery] Guid[]? libraryIds = null)
    {
        var store = Plugin.Instance?.Store;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        TouchInstance();

        var libraryManager = HttpContext.RequestServices.GetRequiredService<ILibraryManager>();
        var userManager = HttpContext.RequestServices.GetRequiredService<IUserManager>();

        limit = Math.Clamp(limit, 1, 200);
        startIndex = Math.Max(startIndex, 0);

        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        // See GetPlaylists — optional scope, validated when present.
        var librarySet = NormalizeLibraryIds(libraryIds);
        if (librarySet != null)
        {
            var libraryError = ValidateLibrariesAccess(librarySet, user, libraryManager);
            if (libraryError != null)
            {
                return libraryError;
            }
        }

        DateTimeOffset? sinceDate = null;
        if (!string.IsNullOrEmpty(since) && DateTimeOffset.TryParse(since, out var parsed))
        {
            sinceDate = parsed;
        }

        DateTimeOffset? untilDate = null;
        if (!string.IsNullOrEmpty(until) && DateTimeOffset.TryParse(until, out var parsedUntil))
        {
            untilDate = SyncClock.ClampUntil(parsedUntil);
        }

        var minSafeSince = store.GetMinSafeSince();
        if (sinceDate.HasValue && minSafeSince.HasValue && sinceDate.Value < minSafeSince.Value)
        {
            Logger.LogWarning(
                "NoriSync {Path} cursor {Since} predates the retention watermark {MinSafeSince}; returning 410 so the client re-runs a full sync",
                HttpContext.Request.Path,
                sinceDate.Value,
                minSafeSince.Value);

            return StatusCode(StatusCodes.Status410Gone, new
            {
                Error = "cursor_expired",
                MinSafeSince = minSafeSince.Value,
                Message = "The provided 'since' cursor is older than the server's retention watermark. Discard local cursor and perform a full sync via /playlists."
            });
        }

        var changesResponse = store.GetChanges(sinceDate, untilDate, new[] { TrackedItemType.Playlist });

        // The change store is an append-only log, so a playlist edited N times yields N records.
        // Collapse to the latest change per playlist id before building the response, otherwise a
        // single playlist would appear repeatedly and inflate TotalCount / break pagination.
        var latestById = new Dictionary<Guid, ChangeRecord>();
        foreach (var change in changesResponse.Changes)
        {
            if (!latestById.TryGetValue(change.ItemId, out var existing)
                || change.Timestamp >= existing.Timestamp)
            {
                latestById[change.ItemId] = change;
            }
        }

        var ownerNameCache = new Dictionary<Guid, string>();
        var changedPlaylists = new List<SyncPlaylistDto>();
        var deletedIds = new HashSet<Guid>();

        foreach (var change in latestById.Values)
        {
            if (change.ChangeType == ChangeType.Deleted)
            {
                deletedIds.Add(change.ItemId);
                continue;
            }

            // Re-evaluate the playlist against this caller at request time. A playlist that was
            // deleted, is no longer an audio playlist, or is no longer visible to this user
            // (unshared/made private) collapses to a tombstone so the client drops its copy.
            var item = libraryManager.GetItemById(change.ItemId);
            if (item is not Playlist playlist
                || playlist.MediaType != MediaType.Audio
                || !playlist.IsVisible(user))
            {
                deletedIds.Add(change.ItemId);
                continue;
            }

            changedPlaylists.Add(BuildPlaylistDto(playlist, user, userManager, ownerNameCache, librarySet, libraryManager));
        }

        changedPlaylists.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        var totalCount = changedPlaylists.Count;
        var pagedPlaylists = changedPlaylists.Skip(startIndex).Take(limit).ToList();

        var response = new PlaylistChangesResponse
        {
            TotalCount = totalCount,
            StartIndex = startIndex,
            Playlists = pagedPlaylists,
            DeletedPlaylistIds = deletedIds.ToList()
        };

        if (Request.Headers.Accept.Any(h => h != null && h.Contains("application/x-msgpack", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = MessagePackSerializer.Serialize(response);
            return File(bytes, "application/x-msgpack");
        }

        return Ok(response);
    }

    /// <summary>
    /// Builds the wire DTO for one playlist. When <paramref name="librarySet"/> is non-null,
    /// entries whose track is not reachable through any of those libraries are dropped and the
    /// remaining entries are renumbered densely, so <c>SortOrder</c> stays a gap-free position
    /// within what the caller will actually store.
    /// </summary>
    private static SyncPlaylistDto BuildPlaylistDto(
        Playlist playlist,
        User user,
        IUserManager userManager,
        Dictionary<Guid, string> ownerNameCache,
        HashSet<Guid>? librarySet,
        ILibraryManager libraryManager)
    {
        // Library membership is decided by the ancestor chain, which every child of the same
        // parent shares — so a playlist of N tracks from M albums costs M ancestor walks, not N.
        var inScopeByParent = new Dictionary<Guid, bool>();
        bool InScope(BaseItem item)
        {
            if (librarySet == null)
            {
                return true;
            }

            var parentId = item.ParentId;
            if (parentId.Equals(Guid.Empty))
            {
                return IsItemInAnyLibrary(item, librarySet, libraryManager);
            }

            if (!inScopeByParent.TryGetValue(parentId, out var inScope))
            {
                inScope = IsItemInAnyLibrary(item, librarySet, libraryManager);
                inScopeByParent[parentId] = inScope;
            }

            return inScope;
        }

        var tracks = playlist.GetLinkedChildren()
            .Where(c => c is Audio && c.IsVisible(user) && InScope(c))
            .Select((c, index) => new PlaylistTrackEntry
            {
                TrackId = c.Id,
                SortOrder = index
            })
            .ToList();

        var ownerUserId = playlist.OwnerUserId;
        if (!ownerNameCache.TryGetValue(ownerUserId, out var ownerUsername))
        {
            ownerUsername = ownerUserId.Equals(Guid.Empty)
                ? string.Empty
                : userManager.GetUserById(ownerUserId)?.Username ?? string.Empty;
            ownerNameCache[ownerUserId] = ownerUsername;
        }

        return new SyncPlaylistDto
        {
            Id = playlist.Id,
            Name = playlist.Name ?? string.Empty,
            Description = playlist.Overview,
            Tracks = tracks,
            OwnerUserId = ownerUserId,
            OwnerUsername = ownerUsername,
            IsOwner = ownerUserId.Equals(user.Id)
        };
    }

    /// <summary>
    /// Builds the artist-name to artist-id lookup used to resolve the artist name strings carried
    /// on albums and tracks, memoised for a short window.
    /// </summary>
    /// <remarks>
    /// This is a whole-server <c>MusicArtist</c> enumeration, and it ran on every request — so a
    /// single full sync paging at 50 repeated it once per page. Only ids are memoised, so artist
    /// metadata is still read fresh from the library for each response.
    /// </remarks>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>The lookup.</returns>
    private ArtistNameLookup BuildArtistLookup(ILibraryManager libraryManager)
    {
        var cache = HttpContext.RequestServices.GetRequiredService<IMemoryCache>();

        if (cache.TryGetValue(ArtistNameLookup.CacheKey, out ArtistNameLookup? cached) && cached != null)
        {
            return cached;
        }

        var lookup = ArtistNameLookup.FromLibrary(libraryManager);
        cache.Set(ArtistNameLookup.CacheKey, lookup, TimeSpan.FromSeconds(60));

        return lookup;
    }

    private static string? GetMusicBrainzArtistId(MusicArtist artist)
    {
        return artist.ProviderIds.TryGetValue(MediaBrowser.Model.Entities.MetadataProvider.MusicBrainzArtist.ToString(), out var id)
            && !string.IsNullOrWhiteSpace(id)
                ? id
                : null;
    }

    private static string? GetMusicBrainzReleaseGroupId(MusicAlbum album)
    {
        return album.ProviderIds.TryGetValue(MediaBrowser.Model.Entities.MetadataProvider.MusicBrainzReleaseGroup.ToString(), out var id)
            && !string.IsNullOrWhiteSpace(id)
                ? id
                : null;
    }

    // The recording MBID lives under the MusicBrainzRecording provider key (Picard's
    // "MusicBrainz Track Id" tag / UFID), NOT MusicBrainzTrack — that key holds the
    // release-specific track id, which differs across releases of the same recording.
    private static string? GetMusicBrainzRecordingId(Audio audio)
    {
        return audio.ProviderIds.TryGetValue(MediaBrowser.Model.Entities.MetadataProvider.MusicBrainzRecording.ToString(), out var id)
            && !string.IsNullOrWhiteSpace(id)
                ? id
                : null;
    }

    // Tags are already loaded with the item row, so this is a scan of an in-memory array with
    // no extra query. Trimmed via span so a stray " Explicit " still matches without allocating.
    internal static bool IsExplicit(string[]? tags)
    {
        if (tags == null)
        {
            return false;
        }

        foreach (var tag in tags)
        {
            if (tag != null && tag.AsSpan().Trim().Equals("Explicit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> SplitGenres(string[]? genres)
    {
        if (genres == null || genres.Length == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in genres)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            foreach (var part in entry.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(part))
                {
                    result.Add(part);
                }
            }
        }

        return result;
    }

    private static List<SyncTrackDto> GetTracks(
        MusicAlbum album,
        User? user,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IImageProcessor imageProcessor,
        ItemImageInfo? albumPrimary,
        ArtistNameLookup artistLookup,
        Dictionary<Guid, MusicArtist> referencedArtists,
        HashSet<Guid>? trackIdFilter = null)
    {
        var trackItems = libraryManager.GetItemList(new InternalItemsQuery
        {
            User = user,
            ParentId = album.Id,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true
        });

        var tracks = new List<SyncTrackDto>();
        var seenTrackIds = new HashSet<Guid>();
        foreach (var item in trackItems)
        {
            if (item is not Audio audio)
            {
                continue;
            }

            if (trackIdFilter != null && !trackIdFilter.Contains(audio.Id))
            {
                continue;
            }

            // Mirrors the album-level guard: never emit the same id twice within one album.
            if (!seenTrackIds.Add(audio.Id))
            {
                continue;
            }

            var liked = false;
            var playCount = 0;
            DateTime? lastPlayedDate = null;
            if (user != null)
            {
                var userData = userDataManager.GetUserData(user, audio);
                if (userData != null)
                {
                    liked = userData.IsFavorite;
                    playCount = userData.PlayCount;
                    lastPlayedDate = userData.LastPlayedDate;
                }
            }

            var audioStream = audio.GetMediaStreams()?.FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio);

            tracks.Add(new SyncTrackDto
            {
                Id = audio.Id,
                Name = audio.Name ?? string.Empty,
                SortName = audio.SortName ?? string.Empty,
                ArtistIds = ResolveArtistIds(audio.Artists, artistLookup, referencedArtists, libraryManager),
                Liked = liked,
                RuntimeTicks = audio.RunTimeTicks ?? 0,
                Container = ResolveCodec(audioStream, audio.Container),
                SampleRate = audioStream?.SampleRate,
                IndexNumber = audio.IndexNumber,
                ParentIndexNumber = audio.ParentIndexNumber,
                PlayCount = playCount,
                LastPlayedDate = lastPlayedDate,
                CoverTag = ResolveTrackCoverTag(audio, albumPrimary, imageProcessor),
                FileSize = audio.Size,
                MusicBrainzRecordingId = GetMusicBrainzRecordingId(audio),
                Explicit = IsExplicit(audio.Tags)
            });
        }

        return tracks;
    }

    // Returns a tag for the track's own Primary image only when it is genuinely distinct
    // from the album's Primary. Most "real albums" embed the same cover into every track,
    // and Jellyfin lifts one of those into the album's Primary; surfacing per-track tags
    // in that case would cause clients to refetch identical bytes per track. The fake-album
    // case the feedback flagged (folder of singles tagged as an album) is preserved because
    // each track's embedded art has a distinct path/blurhash from the album's Primary.
    private static string? ResolveTrackCoverTag(Audio audio, ItemImageInfo? albumPrimary, IImageProcessor imageProcessor)
    {
        var trackPrimary = audio.GetImageInfo(MediaBrowser.Model.Entities.ImageType.Primary, 0);
        if (trackPrimary == null)
        {
            return null;
        }

        if (albumPrimary != null && ImageInfosLikelyEqual(trackPrimary, albumPrimary))
        {
            return null;
        }

        return imageProcessor.GetImageCacheTag(audio, trackPrimary);
    }

    private static bool ImageInfosLikelyEqual(ItemImageInfo a, ItemImageInfo b)
    {
        // Same backing file is the cheapest signal and covers folder.jpg / cover.jpg cases
        // where Jellyfin has the album and tracks pointing at the same on-disk image.
        if (!string.IsNullOrEmpty(a.Path) && !string.IsNullOrEmpty(b.Path)
            && string.Equals(a.Path, b.Path, StringComparison.Ordinal))
        {
            return true;
        }

        // BlurHash is a perceptual hash populated by the image-extraction pipeline. When
        // both sides have one and they match, the images are visually identical even if
        // the underlying files differ (e.g. one is embedded, the other an extracted cache
        // copy). Distinct artworks practically never collide.
        if (!string.IsNullOrEmpty(a.BlurHash) && !string.IsNullOrEmpty(b.BlurHash)
            && string.Equals(a.BlurHash, b.BlurHash, StringComparison.Ordinal))
        {
            return true;
        }

        // Same on-disk dimensions + mtime is a strong tiebreaker when neither path nor
        // blurhash is conclusive — covers re-extracted images that share metadata.
        if (a.Width > 0 && a.Height > 0
            && a.Width == b.Width && a.Height == b.Height
            && a.DateModified == b.DateModified)
        {
            return true;
        }

        return false;
    }

    private static string ResolveCodec(MediaBrowser.Model.Entities.MediaStream? audioStream, string? container)
    {
        if (!string.IsNullOrWhiteSpace(audioStream?.Codec))
        {
            return audioStream!.Codec!;
        }

        if (string.IsNullOrWhiteSpace(container))
        {
            return string.Empty;
        }

        var commaIndex = container.IndexOf(',', StringComparison.Ordinal);
        return commaIndex >= 0 ? container[..commaIndex].Trim() : container;
    }

    private static List<Guid> ResolveArtistIds(
        IReadOnlyList<string>? artistNames,
        ArtistNameLookup artistLookup,
        Dictionary<Guid, MusicArtist> referencedArtists,
        ILibraryManager libraryManager)
    {
        if (artistNames == null || artistNames.Count == 0)
        {
            return new List<Guid>();
        }

        var ids = new List<Guid>();
        foreach (var name in artistNames)
        {
            if (!artistLookup.TryResolve(name, out var artistId))
            {
                continue;
            }

            if (referencedArtists.ContainsKey(artistId))
            {
                ids.Add(artistId);
                continue;
            }

            // Resolved by id rather than held in the memo, so a rename or metadata edit is
            // reflected immediately; this is an in-memory item-cache hit in the common case.
            // The id is only emitted once the artist actually resolves, because the memo can
            // outlive a deleted artist by up to its TTL and clients must never receive an
            // artist id with no matching record in the same response.
            if (libraryManager.GetItemById(artistId) is MusicArtist artist)
            {
                referencedArtists[artistId] = artist;
                ids.Add(artistId);
            }
        }

        return ids;
    }

    private static HashSet<Guid>? NormalizeLibraryIds(Guid[]? libraryIds)
    {
        if (libraryIds == null || libraryIds.Length == 0)
        {
            return null;
        }

        var set = new HashSet<Guid>();
        foreach (var id in libraryIds)
        {
            if (id != Guid.Empty)
            {
                set.Add(id);
            }
        }

        return set.Count == 0 ? null : set;
    }

    private ActionResult? ValidateLibrariesAccess(HashSet<Guid> libraryIds, User? user, ILibraryManager libraryManager)
    {
        foreach (var libraryId in libraryIds)
        {
            var library = libraryManager.GetItemById(libraryId);
            if (library is not CollectionFolder)
            {
                // Usually the client sent a UserView id rather than the CollectionFolder id, which
                // happens when the library's content type is unset or mixed. Logging the resolved
                // type makes that diagnosable from the server log alone.
                Logger.LogWarning(
                    "NoriSync sync rejected: library {LibraryId} is not a CollectionFolder (resolved to {ResolvedType}). Check the library's content type is set to Music, and that the client sends the VirtualFolders ItemId",
                    libraryId,
                    library?.GetType().Name ?? "null");

                return NotFound(new { Error = $"Library {libraryId} not found" });
            }

            if (user != null && !library.IsVisible(user))
            {
                Logger.LogWarning(
                    "NoriSync sync rejected: library {LibraryId} ({LibraryName}) is not visible to user {UserName}",
                    libraryId,
                    library.Name,
                    user.Username);

                return Forbid();
            }
        }

        return null;
    }

    /// <summary>
    /// Logs and returns the 400 raised when a caller omits the required library scope.
    /// </summary>
    /// <param name="endpoint">Endpoint name, for the log message.</param>
    /// <returns>The 400 result.</returns>
    private BadRequestObjectResult MissingLibraryIds(string endpoint)
    {
        Logger.LogWarning(
            "NoriSync {Endpoint} called without libraryIds; returning 400. The client must send the library ids to sync",
            endpoint);

        return BadRequest(new { Error = "Query parameter 'libraryIds' is required." });
    }

    /// <summary>
    /// Logs a full sync that returned no albums, distinguishing a genuinely empty library from one
    /// that holds tracks this API cannot reach.
    /// </summary>
    /// <param name="libraryIds">Libraries that were queried.</param>
    /// <param name="libraryManager">The library manager.</param>
    private void LogEmptySync(HashSet<Guid> libraryIds, ILibraryManager libraryManager)
    {
        var trackTotal = libraryManager.GetItemsResult(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true,
            AncestorIds = libraryIds.ToArray(),
            Limit = 1
        }).TotalRecordCount;

        if (trackTotal > 0)
        {
            // The signature of a flat library: Jellyfin only creates MusicAlbum entities for
            // folders of audio files, and this payload nests tracks inside albums, so loose files
            // at a library root are unreachable. Without this line the symptom is a 200 with an
            // empty body and nothing in the log.
            Logger.LogWarning(
                "NoriSync sync returned no albums for libraries {LibraryIds} even though they hold {TrackCount} tracks. Those tracks are not grouped into album entities, so the sync cannot reach them — tag the files with an album or restructure them into Artist/Album folders and rescan",
                libraryIds,
                trackTotal);
        }
        else
        {
            Logger.LogInformation(
                "NoriSync sync returned no albums for libraries {LibraryIds}; the libraries hold no audio items",
                libraryIds);
        }
    }

    /// <summary>
    /// Returns the ids of every album in the requested library scope, de-duplicated and in a
    /// stable total order, memoised for a short window.
    /// </summary>
    /// <remarks>
    /// The scope has to be resolved whole rather than paged in the query, because the correct
    /// <c>TotalCount</c> and a reproducible page window both depend on the post-query filtering.
    /// Recomputing that on every page would make a full sync quadratic — a 5,000-album library
    /// paged at 50 would resolve collection folders 500,000 times over the ~100 requests. The
    /// memo collapses that to once per sync while keeping the payload itself fresh, since only
    /// ids are cached and each page re-resolves its albums by id.
    /// </remarks>
    /// <param name="user">The requesting user, or null.</param>
    /// <param name="librarySet">The requested library scope.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>Ordered album ids for the scope.</returns>
    private IReadOnlyList<Guid> GetScopedAlbumIds(User? user, HashSet<Guid> librarySet, ILibraryManager libraryManager)
    {
        var cache = HttpContext.RequestServices.GetRequiredService<IMemoryCache>();
        var cacheKey = BuildAlbumScopeCacheKey(user?.Id ?? Guid.Empty, librarySet);

        if (cache.TryGetValue(cacheKey, out IReadOnlyList<Guid>? cached) && cached != null)
        {
            return cached;
        }

        var query = new InternalItemsQuery
        {
            User = user,
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
            AncestorIds = librarySet.ToArray()
        };

        var result = libraryManager.GetItemsResult(query);
        var scopedAlbums = ScopeAlbumsToLibraries(result.Items, librarySet, libraryManager, out var unreachableCount, out var duplicateCount);

        if (unreachableCount > 0 || duplicateCount > 0)
        {
            Logger.LogWarning(
                "NoriSync /albums for libraries {LibraryIds} ({LibraryCount} requested): the ancestor index returned {RawCount} albums but only {ScopedCount} belong to those libraries — dropped {UnreachableCount} unreachable and {DuplicateCount} duplicate ids. Unreachable albums are stale rows left by a library root-folder change; duplicates mean the requested libraries overlap",
                librarySet,
                librarySet.Count,
                result.Items.Count,
                scopedAlbums.Count,
                unreachableCount,
                duplicateCount);
        }

        var ids = scopedAlbums.Select(a => a.Id).ToList();

        // Short and absolute, not sliding: a client paging steadily through a large library must
        // not be able to hold a stale scope open indefinitely. Long enough that one sync shares
        // a single computation, short enough that a library edit shows up promptly.
        cache.Set(cacheKey, (IReadOnlyList<Guid>)ids, TimeSpan.FromSeconds(60));

        return ids;
    }

    /// <summary>
    /// Builds the memo key for an album scope. Library ids are sorted so that callers passing the
    /// same set in a different order share one entry.
    /// </summary>
    /// <param name="userId">The requesting user's id, or <see cref="Guid.Empty"/>.</param>
    /// <param name="librarySet">The requested library scope.</param>
    /// <returns>A cache key.</returns>
    internal static string BuildAlbumScopeCacheKey(Guid userId, HashSet<Guid> librarySet)
    {
        var libraries = librarySet.ToList();
        libraries.Sort();

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"norisync:albumscope:{userId}:{string.Join(',', libraries)}");
    }

    /// <summary>
    /// Reduces a raw ancestor-index query result to the albums that genuinely belong to the
    /// requested libraries, de-duplicated and in a stable total order.
    /// </summary>
    /// <param name="items">Raw query results.</param>
    /// <param name="libraryIds">The requested library scope.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="unreachableCount">Number of albums dropped as not reachable from the scope.</param>
    /// <param name="duplicateCount">Number of albums dropped as repeat ids.</param>
    /// <returns>The scoped albums, ordered deterministically.</returns>
    private static List<MusicAlbum> ScopeAlbumsToLibraries(
        IReadOnlyList<BaseItem> items,
        HashSet<Guid> libraryIds,
        ILibraryManager libraryManager,
        out int unreachableCount,
        out int duplicateCount)
    {
        unreachableCount = 0;
        duplicateCount = 0;

        var seen = new HashSet<Guid>();
        var albums = new List<MusicAlbum>(items.Count);

        foreach (var item in items)
        {
            if (item is not MusicAlbum album)
            {
                continue;
            }

            // Re-derive library membership from the collection folders rather than trusting
            // the ancestor index the query was filtered on. The two disagree when a library's
            // root folder changes: item ids are derived from paths, so the move reissues every
            // id, and the old items can linger with ancestor rows still pointing here.
            if (!IsItemInAnyLibrary(album, libraryIds, libraryManager))
            {
                unreachableCount++;
                continue;
            }

            if (!seen.Add(album.Id))
            {
                duplicateCount++;
                continue;
            }

            albums.Add(album);
        }

        albums.Sort((a, b) => CompareAlbumOrder(a.DateCreated, a.Id, b.DateCreated, b.Id));

        return albums;
    }

    /// <summary>
    /// Orders albums newest-first by creation date, breaking ties on id.
    /// </summary>
    /// <remarks>
    /// DateCreated alone is not a total order — a bulk rescan stamps nearly every album with the
    /// same value — and paging by offset over a non-unique key can return one album on two pages
    /// while skipping another entirely. The id tiebreaker makes the order total, so a page window
    /// is reproducible across the many requests a full sync takes. This cannot be expressed as an
    /// <c>OrderBy</c> on the query: <c>ItemSortBy</c> has no id member in the 10.11 ABI.
    /// </remarks>
    /// <param name="dateCreatedA">First album's creation date.</param>
    /// <param name="idA">First album's id.</param>
    /// <param name="dateCreatedB">Second album's creation date.</param>
    /// <param name="idB">Second album's id.</param>
    /// <returns>A signed comparison result.</returns>
    internal static int CompareAlbumOrder(DateTime dateCreatedA, Guid idA, DateTime dateCreatedB, Guid idB)
    {
        var byDate = dateCreatedB.CompareTo(dateCreatedA);
        return byDate != 0 ? byDate : idA.CompareTo(idB);
    }

    private static bool IsItemInAnyLibrary(BaseItem item, HashSet<Guid> libraryIds, ILibraryManager libraryManager)
    {
        var folders = libraryManager.GetCollectionFolders(item);
        foreach (var folder in folders)
        {
            if (libraryIds.Contains(folder.Id))
            {
                return true;
            }
        }

        return false;
    }

    private User? GetJellyfinUser()
    {
        var claim = User.FindFirstValue("Jellyfin-UserId")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (Guid.TryParse(claim, out var userId))
        {
            var userManager = HttpContext.RequestServices.GetRequiredService<IUserManager>();
            return userManager.GetUserById(userId);
        }

        return null;
    }

    private (Guid UserId, string? DeviceId, string? DeviceName, string? ClientName) GetClientIdentity()
    {
        var userIdClaim = User.FindFirstValue("Jellyfin-UserId")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userId = Guid.TryParse(userIdClaim, out var parsed) ? parsed : Guid.Empty;

        var deviceId = User.FindFirstValue("Jellyfin-DeviceId");
        var deviceName = User.FindFirstValue("Jellyfin-Device");
        var clientName = User.FindFirstValue("Jellyfin-Client");

        if (string.IsNullOrEmpty(deviceId))
        {
            var authHeader = Request.Headers["X-Emby-Authorization"].ToString();
            if (string.IsNullOrEmpty(authHeader))
            {
                authHeader = Request.Headers.Authorization.ToString();
            }

            ParseAuthHeader(authHeader, ref deviceId, ref deviceName, ref clientName);
        }

        return (userId, string.IsNullOrWhiteSpace(deviceId) ? null : deviceId, deviceName, clientName);
    }

    private static void ParseAuthHeader(string header, ref string? deviceId, ref string? deviceName, ref string? clientName)
    {
        if (string.IsNullOrEmpty(header))
        {
            return;
        }

        var schemeIndex = header.IndexOf(' ', StringComparison.Ordinal);
        var payload = schemeIndex > 0 ? header[(schemeIndex + 1)..] : header;

        foreach (var raw in payload.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = raw.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = raw[..eq].Trim();
            var value = raw[(eq + 1)..].Trim().Trim('"');
            if (value.Length == 0)
            {
                continue;
            }

            if (string.IsNullOrEmpty(deviceId) && key.Equals("DeviceId", StringComparison.OrdinalIgnoreCase))
            {
                deviceId = value;
            }
            else if (string.IsNullOrEmpty(deviceName) && key.Equals("Device", StringComparison.OrdinalIgnoreCase))
            {
                deviceName = value;
            }
            else if (string.IsNullOrEmpty(clientName) && key.Equals("Client", StringComparison.OrdinalIgnoreCase))
            {
                clientName = value;
            }
        }
    }

    private void TouchInstance()
    {
        var instanceStore = Plugin.Instance?.InstanceStore;
        if (instanceStore == null)
        {
            return;
        }

        var identity = GetClientIdentity();
        if (identity.UserId == Guid.Empty || string.IsNullOrEmpty(identity.DeviceId))
        {
            return;
        }

        instanceStore.Touch(identity.UserId, identity.DeviceId, identity.DeviceName, identity.ClientName, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Records that the calling client has fully reconciled the change log up to a given server
    /// timestamp. The plugin uses the minimum acknowledged timestamp across registered clients
    /// to decide when change records can be safely purged.
    /// </summary>
    /// <param name="timestamp">The server timestamp (ISO 8601) up to which the client has fully synced.</param>
    /// <returns>No content on success.</returns>
    [HttpPost("sync-ack")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult RecordSyncAck([FromQuery] string timestamp)
    {
        var instanceStore = Plugin.Instance?.InstanceStore;
        if (instanceStore == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrWhiteSpace(timestamp) || !DateTimeOffset.TryParse(timestamp, out var ack))
        {
            return BadRequest(new { Error = "Missing or invalid 'timestamp' query parameter." });
        }

        var now = DateTimeOffset.UtcNow;
        if (ack > now)
        {
            ack = now;
        }

        var identity = GetClientIdentity();
        if (identity.UserId == Guid.Empty || string.IsNullOrEmpty(identity.DeviceId))
        {
            return BadRequest(new { Error = "Client identity (user + device id) could not be resolved from the request." });
        }

        instanceStore.Touch(identity.UserId, identity.DeviceId, identity.DeviceName, identity.ClientName, now);
        instanceStore.RecordAck(identity.UserId, identity.DeviceId, ack, now);
        return NoContent();
    }

    /// <summary>
    /// Lists registered client app instances. Requires administrator privileges.
    /// </summary>
    /// <returns>The registered instances.</returns>
    [HttpGet("instances")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult GetInstances()
    {
        var instanceStore = Plugin.Instance?.InstanceStore;
        if (instanceStore == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Ok(new { Instances = instanceStore.GetAll() });
    }

    /// <summary>
    /// Removes a registered client app instance. Requires administrator privileges.
    /// </summary>
    /// <param name="userId">The Jellyfin user id of the instance.</param>
    /// <param name="deviceId">The Jellyfin device id of the instance.</param>
    /// <returns>No content whether or not a row existed.</returns>
    [HttpDelete("instances/{userId:guid}/{deviceId}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult DeleteInstance(Guid userId, string deviceId)
    {
        var instanceStore = Plugin.Instance?.InstanceStore;
        if (instanceStore == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        instanceStore.Remove(userId, deviceId);
        return NoContent();
    }
}

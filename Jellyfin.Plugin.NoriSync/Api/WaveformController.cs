using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MessagePack;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// API controller exposing the waveform analysis task's progress and a raw feed of waveform
/// records. Unlike <see cref="ChangesController"/>, this endpoint surfaces the feature store
/// directly, keyed by track id, without wrapping albums or applying user visibility filters.
/// </summary>
[ApiController]
[Route("api/norisync/waveform")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize]
public class WaveformController : ControllerBase
{
    /// <summary>
    /// Gets the state of the current or most-recent waveform analysis run, along with the
    /// cumulative count of successfully processed tracks since this counter was introduced.
    /// </summary>
    /// <returns>Current run progress plus lifetime and library counters.</returns>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult GetStatus()
    {
        var store = Plugin.Instance?.FeatureStore;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var snapshot = WaveformProgressTracker.Instance.Snapshot();

        return Ok(new
        {
            Current = snapshot,
            Lifetime = new
            {
                Processed = store.GetLifetimeProcessed(),
                CountingSince = store.GetLifetimeCountingSince()
            },
            Library = new
            {
                Tracks = store.Count()
            }
        });
    }

    /// <summary>
    /// Gets waveform records whose <c>ProcessedAt</c> is strictly after the given timestamp,
    /// ordered ascending. Intended for clients that sync only waveforms, independent of the
    /// album/track change feed.
    /// </summary>
    /// <param name="since">ISO 8601 cursor. Omit to return all records.</param>
    /// <param name="until">Optional inclusive upper bound on <c>ProcessedAt</c> (ISO 8601). Clients should fetch this value once from <c>/api/norisync/timestamp</c> and reuse it across pages so new waveforms written during paging don't leak in. Future timestamps are clamped to the current server time.</param>
    /// <param name="startIndex">Zero-based offset within the result set (default 0).</param>
    /// <param name="limit">Maximum items per page (default 100, max 500).</param>
    /// <param name="libraryIds">Library ids to restrict results to tracks reachable through those libraries. Required; repeat the query parameter for multiple values.</param>
    /// <returns>A page of waveform records.</returns>
    [HttpGet("changes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<WaveformChangesResponse> GetChanges(
        [FromQuery] string? since = null,
        [FromQuery] string? until = null,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 100,
        [FromQuery] Guid[]? libraryIds = null)
    {
        var store = Plugin.Instance?.FeatureStore;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var librarySet = NormalizeLibraryIds(libraryIds);
        if (librarySet == null)
        {
            return BadRequest(new { Error = "Query parameter 'libraryIds' is required." });
        }

        var libraryManager = HttpContext.RequestServices.GetRequiredService<ILibraryManager>();
        var user = GetJellyfinUser();
        var libraryError = ValidateLibrariesAccess(librarySet, user, libraryManager);
        if (libraryError != null)
        {
            return libraryError;
        }

        var sinceDate = DateTimeOffset.MinValue;
        if (!string.IsNullOrEmpty(since))
        {
            if (!DateTimeOffset.TryParse(since, out sinceDate))
            {
                return BadRequest(new { Error = "Invalid 'since' timestamp. Use ISO 8601 (e.g. 2026-04-19T12:34:56Z)." });
            }
        }

        DateTimeOffset? untilDate = null;
        if (!string.IsNullOrEmpty(until))
        {
            if (!DateTimeOffset.TryParse(until, out var parsedUntil))
            {
                return BadRequest(new { Error = "Invalid 'until' timestamp. Use ISO 8601 (e.g. 2026-04-19T12:34:56Z)." });
            }

            untilDate = SyncClock.ClampUntil(parsedUntil);
        }

        limit = Math.Clamp(limit, 1, 500);
        startIndex = Math.Max(startIndex, 0);

        var totalCount = store.CountProcessedSince(sinceDate, untilDate, librarySet);
        var features = store.GetProcessedSince(sinceDate, untilDate, librarySet, limit, startIndex);

        var items = new List<WaveformChangeDto>(features.Count);
        foreach (var f in features)
        {
            items.Add(new WaveformChangeDto
            {
                ItemId = f.ItemId,
                ProcessedAt = f.ProcessedAt,
                SourceMtime = f.SourceMtime,
                Waveform = f.Waveform,
                WaveformVersion = f.WaveformVersion
            });
        }

        var response = new WaveformChangesResponse
        {
            TotalCount = totalCount,
            StartIndex = startIndex,
            Items = items,
            LatestProcessedAt = items.Count > 0 ? items[^1].ProcessedAt : null
        };

        if (Request.Headers.Accept.Any(h => h != null && h.Contains("application/x-msgpack", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = MessagePackSerializer.Serialize(response);
            return File(bytes, "application/x-msgpack");
        }

        return Ok(response);
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
                return NotFound(new { Error = $"Library {libraryId} not found" });
            }

            if (user != null && !library.IsVisible(user))
            {
                return Forbid();
            }
        }

        return null;
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
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// Song requests: a MusicBrainz recording put on SoulSync's wishlist (API_FORMAT.md §3.21). Named
/// for what they are rather than for SoulSync, which is the only song backend so far.
/// </summary>
[ApiController]
[Route("api/norisync/requests/tracks")]
[Authorize]
public class TrackRequestsController : ControllerBase
{
    private readonly TrackRequestService _requests;
    private readonly ILogger<TrackRequestsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackRequestsController"/> class.
    /// </summary>
    /// <param name="requests">The song request service.</param>
    /// <param name="logger">Logger.</param>
    public TrackRequestsController(TrackRequestService requests, ILogger<TrackRequestsController> logger)
    {
        _requests = requests;
        _logger = logger;
    }

    /// <summary>
    /// Lists song requests: the caller's own; every user's (<c>?all=true</c>) or another user's
    /// (<c>?userId=</c>) for an administrator.
    /// </summary>
    /// <param name="userId">Optional user id (admin only).</param>
    /// <param name="all">Every user's (admin only).</param>
    /// <returns>The requests, newest first.</returns>
    [HttpGet]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<TrackRequestRecord>> List([FromQuery] Guid? userId, [FromQuery] bool all = false)
    {
        var user = RequestCallers.Resolve(this);
        if (user == null)
        {
            return Forbid();
        }

        var store = _requests.Store;
        if (store == null)
        {
            return Ok(Array.Empty<TrackRequestRecord>());
        }

        var isAdmin = RequestCallers.IsAdministrator(user);
        if (all || (userId.HasValue && userId.Value != user.Id))
        {
            if (!isAdmin)
            {
                return Forbid();
            }

            return Ok(all ? store.ListAll() : store.ListByUser(userId!.Value));
        }

        return Ok(store.ListByUser(user.Id));
    }

    /// <summary>
    /// Requests a song. The body's hints (title, artist, album…) come from the MusicBrainz result
    /// the app shows; a missing title or artist is read from MusicBrainz. Gated by
    /// <c>LidarrRequireAdmin</c>.
    /// </summary>
    /// <param name="recordingMbid">The MusicBrainz recording id.</param>
    /// <param name="hints">What the app knows about the song; may be empty.</param>
    /// <returns>The caller's request; 422 with SoulSync's reason when it refused; 409 when it added nothing.</returns>
    [HttpPost("{recordingMbid}")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Create(string recordingMbid, [FromBody] TrackRequestHints? hints)
        => RequestAsync(recordingMbid, hints, retry: false);

    /// <summary>
    /// Asks again for a song already requested (after a failure): it goes back on SoulSync's
    /// wishlist with what was stored when it was asked, and a wishlist run starts.
    /// </summary>
    /// <param name="recordingMbid">The MusicBrainz recording id.</param>
    /// <returns>The caller's request.</returns>
    [HttpPost("{recordingMbid}/retry")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Retry(string recordingMbid)
        => RequestAsync(recordingMbid, null, retry: true);

    /// <summary>
    /// Cancels the caller's request (an administrator's cancels every user's). The song leaves
    /// SoulSync's wishlist once nobody wants it.
    /// </summary>
    /// <param name="recordingMbid">The MusicBrainz recording id.</param>
    /// <returns>204, or 404 when the caller has no such request.</returns>
    [HttpDelete("{recordingMbid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(string recordingMbid)
    {
        var user = RequestCallers.Resolve(this);
        if (user == null)
        {
            return Forbid();
        }

        var mbid = TrackRequestService.NormalizeMbid(recordingMbid);
        if (mbid == null)
        {
            return NotFound();
        }

        var cancelled = await _requests.CancelAsync(user.Id, mbid, RequestCallers.IsAdministrator(user), HttpContext.RequestAborted).ConfigureAwait(false);
        return cancelled ? NoContent() : NotFound();
    }

    private async Task<IActionResult> RequestAsync(string recordingMbid, TrackRequestHints? hints, bool retry)
    {
        if (!_requests.IsConfigured)
        {
            return Unavailable("SoulSync is not configured on this server.");
        }

        var store = _requests.Store;
        if (store == null)
        {
            return Unavailable("Plugin not initialized.");
        }

        var user = RequestCallers.Resolve(this);
        if (user == null || !RequestCallers.MayRequest(user))
        {
            return Forbid();
        }

        var mbid = TrackRequestService.NormalizeMbid(recordingMbid);
        if (mbid == null)
        {
            return BadRequest(new { Error = "A MusicBrainz recording id is required." });
        }

        if (retry)
        {
            var others = store.ListByRecording(mbid);
            var existing = store.Get(user.Id, mbid) ?? (others.Count > 0 ? others[0] : null);
            if (existing == null)
            {
                return NotFound(new { Error = "There is no request for that song." });
            }

            hints = new TrackRequestHints
            {
                Title = existing.Title,
                Artist = existing.ArtistName,
                Album = existing.AlbumTitle,
                ReleaseGroupMbid = existing.ReleaseGroupMbid,
            };
        }

        try
        {
            var outcome = await _requests.RequestAsync(user.Id, mbid, hints, DateTimeOffset.UtcNow, HttpContext.RequestAborted).ConfigureAwait(false);
            return outcome.Kind switch
            {
                TrackRequestOutcomeKind.Requested => Ok(outcome.Record),
                TrackRequestOutcomeKind.Refused => UnprocessableEntity(new { Error = outcome.Message, Code = "refused" }),
                TrackRequestOutcomeKind.NotAdded => Conflict(new { Error = outcome.Message, Code = "not_added" }),
                _ => NotFound(new { Error = outcome.Message }),
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "SoulSync or MusicBrainz unreachable while requesting song {Mbid}", mbid);
            return Unavailable("SoulSync is unreachable.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Unexpected answer while requesting song {Mbid}", mbid);
            return Unavailable("SoulSync returned an unexpected response.");
        }
        catch (TaskCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Song request for {Mbid} timed out", mbid);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { Error = "SoulSync timed out." });
        }
    }

    private ObjectResult Unavailable(string message)
        => StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = message });
}

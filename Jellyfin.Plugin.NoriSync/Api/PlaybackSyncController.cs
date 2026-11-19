using System;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// Playback sync between a user's devices (API_FORMAT §3.20): one report endpoint in place of
/// Jellyfin's <c>Sessions/Playing*</c>, the user's devices, the queues their sessions name, and a
/// stop. Changes reach the user's other devices as pushes (<see cref="PlaybackHub"/>).
/// </summary>
[ApiController]
[Route("api/norisync/playback")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize]
public class PlaybackSyncController : ControllerBase
{
    /// <summary>
    /// What <c>GET /info</c> lists in <c>Features</c> while these endpoints exist.
    /// </summary>
    public const string Feature = "playback";

    private const int MaxDeviceFieldLength = 256;

    private readonly PlaybackHub _hub;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSyncController"/> class.
    /// </summary>
    /// <param name="hub">The playback hub.</param>
    public PlaybackSyncController(PlaybackHub hub)
    {
        _hub = hub;
    }

    /// <summary>
    /// Records a device's report, pushes it to the user's other devices, then passes the Jellyfin
    /// event on to the device's Jellyfin session (not for a <c>Cursor</c>, nor a stale report).
    /// </summary>
    /// <param name="body">The report.</param>
    /// <returns><c>204</c>, or <c>200</c> with <see cref="PlaybackReportResponse"/> when the device has something to do.</returns>
    [HttpPost("report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Report([FromBody] PlaybackReportRequest? body)
    {
        if (!_hub.IsAvailable)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!PlaybackReportRequest.TryParse(body, out var report, out var error))
        {
            return BadRequest(new { Error = error });
        }

        if (!TryGetCaller(out var userId, out var deviceId, out var deviceName))
        {
            return Forbid();
        }

        var (outcome, _) = _hub.Report(userId, deviceId, deviceName, report!);
        if (outcome.Stale)
        {
            return Ok(new PlaybackReportResponse { Stale = true });
        }

        if (report!.Event != PlaybackEvent.Cursor)
        {
            await ForwardToJellyfin(userId, deviceId, deviceName, report).ConfigureAwait(false);
        }

        var movedTo = outcome.Moved is { } moved && report.Status == PlaybackStatus.Playing && moved.MovedTo != null
            ? new PlaybackMovedTo { DeviceId = moved.MovedTo, DeviceName = moved.MovedToName, SessionId = moved.MovedSessionId }
            : null;
        return outcome.QueueNeeded || movedTo != null
            ? Ok(new PlaybackReportResponse { QueueNeeded = outcome.QueueNeeded, MovedTo = movedTo })
            : NoContent();
    }

    /// <summary>
    /// Every device of the user: its session, where it is, what it is doing, and whether it is live.
    /// </summary>
    /// <returns>The devices.</returns>
    [HttpGet("devices")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<PlaybackDevicesResponse> Devices()
    {
        if (!_hub.IsAvailable)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!TryGetCaller(out var userId, out var deviceId, out _))
        {
            return Forbid();
        }

        Response.Headers.CacheControl = "no-store";
        return _hub.Devices(userId, deviceId);
    }

    /// <summary>
    /// The queue a session holds at a revision: immutable, served as the device sent it.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="rev">The revision.</param>
    /// <returns>The queue's JSON, or <c>404</c> once no device's record names it.</returns>
    [HttpGet("queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult Queue([FromQuery] string? session, [FromQuery] int rev)
    {
        if (!_hub.IsAvailable)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!TryGetCaller(out var userId, out _, out _))
        {
            return Forbid();
        }

        if (string.IsNullOrEmpty(session) || _hub.Queue(userId, session, rev) is not { } queue)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Content(queue, MediaTypeNames.Application.Json);
    }

    /// <summary>
    /// The device stopped <paramref name="session"/>: its session is cleared if it still holds it.
    /// </summary>
    /// <param name="session">The session stopped.</param>
    /// <returns><c>204</c> either way.</returns>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult Stop([FromQuery] string? session)
    {
        if (!_hub.IsAvailable)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!TryGetCaller(out var userId, out var deviceId, out _))
        {
            return Forbid();
        }

        if (!string.IsNullOrEmpty(session))
        {
            _ = _hub.Stop(userId, deviceId, session);
        }

        return NoContent();
    }

    private bool TryGetCaller(out Guid userId, out string deviceId, out string? deviceName)
    {
        var identity = RequestIdentity.Resolve(User, Request);
        userId = identity.UserId;
        deviceId = identity.DeviceId ?? string.Empty;
        deviceName = identity.DeviceName is { Length: > MaxDeviceFieldLength } name ? name[..MaxDeviceFieldLength] : identity.DeviceName;
        return userId != Guid.Empty
            && deviceId.Length is > 0 and <= MaxDeviceFieldLength
            && HttpContext.RequestServices.GetRequiredService<IUserManager>().GetUserById(userId) != null;
    }

    /// <summary>
    /// The report as Jellyfin's own <c>PlaystateController</c> would take it: on the session it
    /// would find (same client, version, device and address), with the fields its bodies carry.
    /// </summary>
    private async Task ForwardToJellyfin(Guid userId, string deviceId, string? deviceName, PlaybackReport report)
    {
        var services = HttpContext.RequestServices;
        var user = services.GetRequiredService<IUserManager>().GetUserById(userId);
        if (user == null)
        {
            return;
        }

        var identity = RequestIdentity.Resolve(User, Request);
        var sessionManager = services.GetRequiredService<ISessionManager>();
        var session = await sessionManager.LogSessionActivity(
            identity.ClientName ?? string.Empty,
            User.FindFirstValue("Jellyfin-Version") ?? string.Empty,
            deviceId,
            deviceName ?? string.Empty,
            HttpContext.GetNormalizedRemoteIP().ToString(),
            user).ConfigureAwait(false);
        var isPaused = report.Status != PlaybackStatus.Playing;

        switch (report.Event)
        {
            case PlaybackEvent.Start:
                await sessionManager.OnPlaybackStart(new PlaybackStartInfo
                {
                    ItemId = report.ItemId,
                    PositionTicks = report.PositionTicks,
                    IsPaused = isPaused,
                    IsMuted = false,
                    PlayMethod = ValidatePlayMethod(report.PlayMethod, report.PlaySessionId, services.GetRequiredService<ITranscodeManager>().GetTranscodingJob),
                    PlaySessionId = report.PlaySessionId,
                    SessionId = session.Id
                }).ConfigureAwait(false);
                break;

            case PlaybackEvent.Progress:
                await sessionManager.OnPlaybackProgress(new PlaybackProgressInfo
                {
                    ItemId = report.ItemId,
                    PositionTicks = report.PositionTicks,
                    IsPaused = isPaused,
                    IsMuted = false,
                    PlayMethod = ValidatePlayMethod(report.PlayMethod, report.PlaySessionId, services.GetRequiredService<ITranscodeManager>().GetTranscodingJob),
                    PlaySessionId = report.PlaySessionId,
                    SessionId = session.Id
                }).ConfigureAwait(false);
                break;

            case PlaybackEvent.Stopped:
                // As Jellyfin's stop report: the play session's transcode goes with it.
                if (report.PlaySessionId != null)
                {
                    await services.GetRequiredService<ITranscodeManager>()
                        .KillTranscodingJobs(deviceId, report.PlaySessionId, _ => true).ConfigureAwait(false);
                }

                await sessionManager.OnPlaybackStopped(new PlaybackStopInfo
                {
                    ItemId = report.ItemId,
                    PositionTicks = report.PositionTicks,
                    PlaySessionId = report.PlaySessionId,
                    SessionId = session.Id
                }).ConfigureAwait(false);
                break;

            case PlaybackEvent.Cursor:
                break;
        }
    }

    /// <summary>
    /// As Jellyfin's <c>PlaystateController</c> does: <c>Transcode</c> (also what an absent
    /// <c>PlayMethod</c> reads as) with no transcoding job for the play session is direct play.
    /// </summary>
    /// <param name="method">The method reported.</param>
    /// <param name="playSessionId">The play session, if any.</param>
    /// <param name="findJob">Jellyfin's transcoding job lookup by play session.</param>
    /// <returns>The method to pass on.</returns>
    internal static PlayMethod ValidatePlayMethod(PlayMethod method, string? playSessionId, Func<string, object?> findJob)
    {
        if (method != PlayMethod.Transcode)
        {
            return method;
        }

        return playSessionId != null && findJob(playSessionId) != null ? PlayMethod.Transcode : PlayMethod.DirectPlay;
    }
}

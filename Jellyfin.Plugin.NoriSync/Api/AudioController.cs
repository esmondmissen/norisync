using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MessagePack;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// Exposes exact, server-derived audio-decode metadata for individual tracks. The server has
/// direct file access plus the bundled ffprobe binary, so it can return the exact playable frame
/// count and the encoder delay/padding — information the stock Jellyfin API cannot provide and
/// that lets the client do precise EOF detection and gapless playback instead of approximating
/// the length from <c>RunTimeTicks</c>.
/// </summary>
[ApiController]
[Route("api/norisync/audio")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize]
public class AudioController : ControllerBase
{
    /// <summary>
    /// Gets exact decode metadata for an audio track. Results are cached server-side and
    /// invalidated by the source file's mtime.
    /// </summary>
    /// <param name="id">The audio item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The track's exact audio info.</returns>
    [HttpGet("{id}/info")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AudioInfoDto>> GetAudioInfo(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var store = Plugin.Instance?.AudioInfoStore;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        var libraryManager = HttpContext.RequestServices.GetRequiredService<ILibraryManager>();
        var item = libraryManager.GetItemById(id);
        if (item is not Audio audio)
        {
            return NotFound();
        }

        if (!audio.IsVisibleStandalone(user))
        {
            return Forbid();
        }

        var path = audio.Path;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return NotFound(new { Error = "Track has no readable local file." });
        }

        var mtime = GetMtime(path);

        var info = store.Get(id, mtime, AudioInfoProbe.Version);
        if (info == null)
        {
            var probe = HttpContext.RequestServices.GetRequiredService<AudioInfoProbe>();
            info = await probe.ProbeAsync(path, audio.RunTimeTicks ?? 0, cancellationToken).ConfigureAwait(false);
            if (info == null)
            {
                return UnprocessableEntity(new { Error = "Could not probe audio file." });
            }

            store.Upsert(id, info, mtime, AudioInfoProbe.Version);
        }

        if (Request.Headers.Accept.Any(h => h != null && h.Contains("application/x-msgpack", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = MessagePackSerializer.Serialize(info, cancellationToken: cancellationToken);
            return File(bytes, "application/x-msgpack");
        }

        return Ok(info);
    }

    private static long GetMtime(string path)
    {
        try
        {
            return System.IO.File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (IOException)
        {
            return 0;
        }
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

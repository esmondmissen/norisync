using System;
using System.Net.Mime;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// Hands out each user's DJ library id: the secret GUID that keys the user's library on the
/// external DJ service. The server is the source of truth, so every device a user signs in on —
/// and every reinstall — resolves to the same library instead of uploading a duplicate.
/// </summary>
[ApiController]
[Route("api/norisync/dj")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize]
public class DjLibraryController : ControllerBase
{
    /// <summary>
    /// Gets the calling user's DJ library id, assigning one on first call. A client that already
    /// minted an id locally can offer it as <see cref="DjLibraryClaimRequest.ProposedLibraryId"/>;
    /// it is adopted only when the user has no id yet and no other user owns it. Otherwise the
    /// stored id is returned, or a fresh one is minted. The returned id is a bearer credential for
    /// the DJ service and is only ever returned to the user it belongs to.
    /// </summary>
    /// <param name="body">Optional request body carrying a proposed library id.</param>
    /// <returns>The user's library id and how it was arrived at.</returns>
    [HttpPost("library")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult ClaimLibrary(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DjLibraryClaimRequest? body)
    {
        var store = Plugin.Instance?.DjLibraryStore;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        Guid? proposal = null;
        var proposedText = body?.ProposedLibraryId;
        if (!string.IsNullOrWhiteSpace(proposedText))
        {
            if (!Guid.TryParse(proposedText, out var parsed) || parsed == Guid.Empty)
            {
                return BadRequest(new { Error = "ProposedLibraryId must be a non-empty GUID." });
            }

            proposal = parsed;
        }

        var claim = store.Claim(user.Id, proposal, DateTimeOffset.UtcNow);

        return Ok(new
        {
            LibraryId = claim.LibraryId.ToString("D"),
            Outcome = claim.Outcome.ToString().ToLowerInvariant()
        });
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

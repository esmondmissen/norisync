using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// API controller that lets the creator of a playlist update its primary image, and keeps the
/// cover designs of playlists whose cover was made in Nori's cover designer.
/// Jellyfin's built-in <c>POST /Items/{itemId}/Images/{imageType}</c> endpoint requires
/// administrator privileges, which prevents ordinary users from updating cover art on
/// playlists they created. This controller allows the playlist owner (or an admin) to
/// upload a new primary image without elevation.
/// </summary>
[ApiController]
[Route("api/norisync/playlists")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize]
public class PlaylistImageController : ControllerBase
{
    /// <summary>
    /// Sets the primary image of a playlist. The caller must be the playlist owner
    /// or an administrator. The request body is the raw image bytes and the
    /// <c>Content-Type</c> header must be an <c>image/*</c> mime type.
    /// </summary>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content on success.</returns>
    [HttpPost("{playlistId}/image/primary")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetPlaylistPrimaryImage(
        [FromRoute] Guid playlistId,
        CancellationToken cancellationToken)
    {
        var access = ResolveWritablePlaylist(playlistId, out var playlist);
        if (access != null)
        {
            return access;
        }

        var contentType = Request.ContentType;
        if (!IsImageContentType(contentType))
        {
            return BadRequest(new { Error = "Content-Type must be an image/* mime type (e.g. image/jpeg)." });
        }

        // Buffer to a seekable stream: IProviderManager.SaveImage needs to read length/position.
        using var buffered = new MemoryStream();
        await Request.Body.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
        if (buffered.Length == 0)
        {
            return BadRequest(new { Error = "Request body was empty." });
        }

        await SaveCoverImage(playlist!, buffered, contentType!, cancellationToken).ConfigureAwait(false);

        // A plain image (a photo) is not a designed cover. The design would stop being served
        // anyway (its image identity no longer matches), but dropping it keeps the table honest.
        Plugin.Instance?.CoverDesignStore.Delete(playlist!.Id);

        return NoContent();
    }

    /// <summary>
    /// Sets the primary image of a playlist to a cover made in Nori's cover designer and stores
    /// the design it was baked from, so other devices can draw the designed page. The two arrive
    /// in one request so they can never describe different covers. Same access rule as
    /// <see cref="SetPlaylistPrimaryImage"/>.
    /// </summary>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="image">The baked cover (an <c>image/*</c> part).</param>
    /// <param name="design">The design document, a JSON object of at most
    /// <see cref="PlaylistCoverDesignStore.MaxDesignLength"/> characters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content on success.</returns>
    [HttpPost("{playlistId}/cover")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetPlaylistDesignedCover(
        [FromRoute] Guid playlistId,
        [FromForm] IFormFile? image,
        [FromForm] string? design,
        CancellationToken cancellationToken)
    {
        var access = ResolveWritablePlaylist(playlistId, out var playlist);
        if (access != null)
        {
            return access;
        }

        if (image == null || image.Length == 0 || !IsImageContentType(image.ContentType))
        {
            return BadRequest(new { Error = "An 'image' part with an image/* Content-Type is required." });
        }

        if (!IsValidDesign(design))
        {
            return BadRequest(new { Error = $"A 'design' part holding a JSON object of at most {PlaylistCoverDesignStore.MaxDesignLength} characters is required." });
        }

        var store = Plugin.Instance?.CoverDesignStore;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        using var buffered = new MemoryStream();
        await image.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
        await SaveCoverImage(playlist!, buffered, image.ContentType, cancellationToken).ConfigureAwait(false);

        // Stamped with the image Jellyfin just recorded, so the design is served only while the
        // playlist still shows exactly this file.
        var saved = playlist!.GetImageInfo(ImageType.Primary, 0);
        if (saved?.Path == null)
        {
            store.Delete(playlist.Id);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        store.Save(playlist.Id, design!, saved.Path, saved.DateModified, DateTimeOffset.UtcNow);
        return NoContent();
    }

    /// <summary>
    /// Returns the cover designs of every audio playlist the caller can see whose cover is still
    /// the image its design was baked into. Supports <c>If-None-Match</c>: the list is fetched on
    /// every sync, and usually hasn't changed.
    /// </summary>
    /// <returns>The designs, or <c>304</c> when the caller's copy is current.</returns>
    [HttpGet("cover-designs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<PlaylistCoverDesignsResponse> GetCoverDesigns()
    {
        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        var store = Plugin.Instance?.CoverDesignStore;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var libraryManager = HttpContext.RequestServices.GetRequiredService<ILibraryManager>();
        var imageProcessor = HttpContext.RequestServices.GetRequiredService<IImageProcessor>();

        var designs = new List<PlaylistCoverDesignDto>();
        foreach (var record in store.GetAll())
        {
            if (libraryManager.GetItemById(record.PlaylistId) is not Playlist playlist
                || playlist.MediaType != MediaType.Audio
                || !playlist.IsVisible(user))
            {
                continue;
            }

            var primary = playlist.GetImageInfo(ImageType.Primary, 0);
            if (primary == null || !PlaylistCoverDesignStore.Describes(record, primary.Path, primary.DateModified))
            {
                continue;
            }

            designs.Add(new PlaylistCoverDesignDto
            {
                PlaylistId = playlist.Id,
                ImageTag = imageProcessor.GetImageCacheTag(playlist, primary),
                Design = record.Design
            });
        }

        var etag = EntityTag(designs);
        Response.Headers.ETag = etag;
        if (Request.Headers.IfNoneMatch.Contains(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return new PlaylistCoverDesignsResponse { Designs = designs };
    }

    /// <summary>
    /// Whether <paramref name="design"/> is acceptable as a stored design document: a JSON
    /// object within <see cref="PlaylistCoverDesignStore.MaxDesignLength"/>. Its contents are the
    /// client's business — every client validates what it draws.
    /// </summary>
    /// <param name="design">The candidate document.</param>
    /// <returns><see langword="true"/> if it may be stored.</returns>
    internal static bool IsValidDesign(string? design)
    {
        if (string.IsNullOrWhiteSpace(design) || design.Length > PlaylistCoverDesignStore.MaxDesignLength)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(design);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// A strong entity tag over exactly what the response carries.
    /// </summary>
    /// <param name="designs">The designs being served.</param>
    /// <returns>The quoted entity tag.</returns>
    internal static string EntityTag(IReadOnlyList<PlaylistCoverDesignDto> designs)
    {
        var builder = new StringBuilder();
        foreach (var design in designs)
        {
            builder.Append(design.PlaylistId.ToString("N", CultureInfo.InvariantCulture))
                .Append('\n').Append(design.ImageTag)
                .Append('\n').Append(design.Design)
                .Append('\0');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "\"" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + "\"";
    }

    private static bool IsImageContentType(string? contentType)
        => !string.IsNullOrWhiteSpace(contentType)
            && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private async Task SaveCoverImage(Playlist playlist, MemoryStream image, string contentType, CancellationToken cancellationToken)
    {
        var providerManager = HttpContext.RequestServices.GetRequiredService<IProviderManager>();
        image.Position = 0;
        await providerManager.SaveImage(playlist, image, contentType, ImageType.Primary, null, cancellationToken)
            .ConfigureAwait(false);
        await playlist.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the playlist and checks the caller may change its cover: the owner or an admin.
    /// </summary>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="playlist">The playlist, when access is granted.</param>
    /// <returns><see langword="null"/> when access is granted; otherwise the error to return.</returns>
    private ActionResult? ResolveWritablePlaylist(Guid playlistId, out Playlist? playlist)
    {
        var libraryManager = HttpContext.RequestServices.GetRequiredService<ILibraryManager>();
        playlist = libraryManager.GetItemById(playlistId) as Playlist;
        if (playlist == null)
        {
            return NotFound();
        }

        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        var isAdmin = user.HasPermission(PermissionKind.IsAdministrator);
        if (!isAdmin && !playlist.OwnerUserId.Equals(user.Id))
        {
            return Forbid();
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

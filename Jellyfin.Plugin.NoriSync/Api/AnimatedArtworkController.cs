using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// Stores and serves animated artwork (short looping videos, typically MP4) for music albums and
/// artists. A companion app uploads the loops with administrator credentials; clients fetch them
/// from the serve endpoint, which streams the raw file with HTTP range support so it bypasses
/// Jellyfin's static-image pipeline (which would otherwise flatten the animation).
/// </summary>
[ApiController]
[Route("api/norisync/artwork")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize]
public class AnimatedArtworkController : ControllerBase
{
    // Names the slot the serve endpoint actually streamed, which differs from the requested one
    // when a fallback kicks in.
    private const string ServedSlotHeader = "X-NoriSync-Artwork-Slot";

    // The pixel dimensions of what was actually streamed, as WIDTHxHEIGHT. A rectangle rather than a
    // single number on purpose: one number cannot describe both a 1080x1440 cover and a 1920x1080
    // artist loop, and reading it as either edge is a guess. Omitted when the size is unknown.
    private const string ServedSizeHeader = "X-NoriSync-Artwork-Size";

    private static readonly HashSet<string> AllowedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Primary", "Backdrop", "Logo", "Thumb", ArtworkShape.TallImageType
    };

    private static readonly Dictionary<string, string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = "video/mp4",
        ["video/quicktime"] = "video/quicktime",
        ["video/webm"] = "video/webm"
    };

    /// <summary>
    /// Uploads (or replaces) the animated artwork for one image slot of an album or artist. The
    /// request body is the raw video bytes and <c>Content-Type</c> must be one of the accepted video
    /// MIME types. Requires administrator privileges.
    /// </summary>
    /// <param name="itemId">The album or artist id.</param>
    /// <param name="imageType">The image slot (<c>Primary</c>, <c>Tall</c>, <c>Backdrop</c>,
    /// <c>Logo</c>, <c>Thumb</c>). An album's two loops live in <c>Primary</c> (square) and
    /// <c>Tall</c> (portrait); a mismatched aspect ratio is warned about, not rejected.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored asset's metadata.</returns>
    [HttpPost("{itemId:guid}/{imageType}")]
    [Authorize(Policy = "RequiresElevation")]
    [DisableRequestSizeLimit]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Upload(
        [FromRoute] Guid itemId,
        [FromRoute] string imageType,
        CancellationToken cancellationToken)
    {
        var store = Plugin.Instance?.AnimatedArtworkStore;
        var config = Plugin.Instance?.Configuration;
        if (store == null || config == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!config.AnimatedArtworkEnabled)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Error = "Animated artwork is disabled in the plugin configuration." });
        }

        var canonicalImageType = NormalizeImageType(imageType);
        if (canonicalImageType == null)
        {
            return BadRequest(new { Error = $"Unsupported image type '{imageType}'. Allowed: {string.Join(", ", AllowedImageTypes)}." });
        }

        var contentType = NormalizeContentType(Request.ContentType);
        if (contentType == null)
        {
            return StatusCode(
                StatusCodes.Status415UnsupportedMediaType,
                new { Error = $"Content-Type must be one of: {string.Join(", ", AllowedContentTypes.Keys)}." });
        }

        var libraryManager = HttpContext.RequestServices.GetRequiredService<ILibraryManager>();
        var item = libraryManager.GetItemById(itemId);
        var itemType = item switch
        {
            MusicAlbum => "Album",
            MusicArtist => "Artist",
            _ => null
        };
        if (itemType == null)
        {
            return NotFound(new { Error = "Item not found, or is not a music album or artist." });
        }

        var tempPath = store.NewTempPath();
        var committed = false;
        try
        {
            var written = await CopyToTempAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (written == 0)
            {
                return BadRequest(new { Error = "Request body was empty." });
            }

            var probe = HttpContext.RequestServices.GetRequiredService<ArtworkProbe>();
            var probed = await probe.ProbeAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (probed == null)
            {
                return UnprocessableEntity(new { Error = "File is not a readable MP4/video with a video stream." });
            }

            // Uploads are stored at full quality exactly as received; sized renditions are cut from
            // this master on request instead. The one rewrite is a lossless faststart remux
            // (stream copy, alpha preserved) for MP4-family sources with the moov at the back, so
            // the stored master can start streaming before the whole file downloads; if it fails
            // the moov_not_at_front warning below still fires.
            var served = probed;
            var servedContentType = contentType;
            var warnings = new List<string>();

            if (served.MoovAtFront == false)
            {
                var transcoder = HttpContext.RequestServices.GetRequiredService<ArtworkTranscoder>();
                var rewritten = await RewriteAsync(
                    store,
                    probe,
                    tempPath,
                    (src, dest) => transcoder.RemuxFaststartAsync(src, dest, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                if (rewritten != null)
                {
                    (tempPath, served) = rewritten.Value;
                    servedContentType = "video/mp4";
                }
            }

            var sha = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);

            var record = new AnimatedArtworkRecord
            {
                ItemId = itemId,
                ItemType = itemType,
                ImageType = canonicalImageType,
                ContentType = servedContentType,
                ByteLength = new FileInfo(tempPath).Length,
                DurationSeconds = served.DurationSeconds,
                Width = served.Width,
                Height = served.Height,
                MoovAtFront = served.MoovAtFront,
                Sha256 = sha,
                UploadedAt = DateTimeOffset.UtcNow,
                UploadedBy = GetJellyfinUser()?.Id.ToString("N")
            };

            store.Commit(record, tempPath);
            committed = true;

            if (served.MoovAtFront == false)
            {
                warnings.Add("moov_not_at_front");
            }

            if (!string.Equals(served.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(served.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("uncommon_video_codec");
            }

            // Advisory only: the slot the uploader picked wins, but a square clip in the tall slot
            // (or vice versa) will letterbox on the surface it was meant for, so say so.
            var shapeWarning = ArtworkShape.GetMismatchWarning(canonicalImageType, itemType, served.Width, served.Height);
            if (shapeWarning != null)
            {
                warnings.Add(shapeWarning);
            }

            return Ok(new
            {
                record.ItemId,
                ItemType = record.ItemType,
                ImageType = record.ImageType,
                record.ContentType,
                VideoCodec = served.VideoCodec,
                record.ByteLength,
                record.DurationSeconds,
                record.Width,
                record.Height,
                record.MoovAtFront,
                BitRateBps = served.BitRateBps,
                Tag = record.Sha256,
                record.UploadedAt,
                Warnings = warnings
            });
        }
        finally
        {
            if (!committed)
            {
                TryDeleteTemp(tempPath);
            }
        }
    }

    /// <summary>
    /// Deletes the animated artwork for one image slot of an album or artist. Requires administrator
    /// privileges. Idempotent: returns <c>204</c> whether or not an asset existed.
    /// </summary>
    /// <param name="itemId">The album or artist id.</param>
    /// <param name="imageType">The image slot.</param>
    /// <returns>No content.</returns>
    [HttpDelete("{itemId:guid}/{imageType}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult Delete([FromRoute] Guid itemId, [FromRoute] string imageType)
    {
        var store = Plugin.Instance?.AnimatedArtworkStore;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var canonicalImageType = NormalizeImageType(imageType);
        if (canonicalImageType == null)
        {
            return BadRequest(new { Error = $"Unsupported image type '{imageType}'." });
        }

        store.Delete(itemId, canonicalImageType);
        return NoContent();
    }

    /// <summary>
    /// Streams the animated artwork for an album or artist. Range requests are honoured so a client
    /// video player can start and loop before the whole file is downloaded. The caller must be able
    /// to see the item; authenticate with the standard Jellyfin token (<c>Authorization</c> header, or <c>?ApiKey=</c> where headers can't be set).
    /// </summary>
    /// <param name="itemId">The album or artist id.</param>
    /// <param name="imageType">The image slot.</param>
    /// <param name="fallback">An optional second slot to serve when <paramref name="imageType"/> is
    /// empty, e.g. <c>Primary</c> when asking for <c>Tall</c>. Lets a player that can only be handed
    /// a URL (AVPlayer) get the best available loop in one request instead of probing for a 404.
    /// The <c>X-NoriSync-Artwork-Slot</c> response header names the slot actually served.</param>
    /// <param name="quality">An optional quality override: a short-edge size in pixels — the
    /// <c>1080p</c> sense, so <c>720</c> is 1280x720 for a 16:9 loop and 720x960 for a 3:4 cover —
    /// rounded up to a fixed ladder step, cut from the master once and cached; or <c>original</c>
    /// for the stored master untouched. Omitted, the server's configured Quality is served. Sizes at
    /// or above the master, and sizes that cannot be produced, serve the master. The
    /// <c>X-NoriSync-Artwork-Size</c> response header gives the dimensions actually served.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The artwork file stream.</returns>
    [HttpGet("{itemId:guid}/{imageType}")]
    [HttpHead("{itemId:guid}/{imageType}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Get(
        [FromRoute] Guid itemId,
        [FromRoute] string imageType,
        [FromQuery] string? fallback = null,
        [FromQuery] string? quality = null,
        CancellationToken cancellationToken = default)
    {
        var store = Plugin.Instance?.AnimatedArtworkStore;
        var config = Plugin.Instance?.Configuration;
        if (store == null || config == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!config.AnimatedArtworkEnabled)
        {
            return NotFound();
        }

        var canonicalImageType = NormalizeImageType(imageType);
        if (canonicalImageType == null)
        {
            return NotFound();
        }

        string? canonicalFallback = null;
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            canonicalFallback = NormalizeImageType(fallback);
            if (canonicalFallback == null)
            {
                return BadRequest(new { Error = $"Unsupported fallback image type '{fallback}'. Allowed: {string.Join(", ", AllowedImageTypes)}." });
            }
        }

        // An explicit quality wins over the server's configured target — including "original",
        // which is the stored master untouched. A null target edge means original from here on.
        int? targetEdge;
        if (string.IsNullOrWhiteSpace(quality))
        {
            targetEdge = config.AnimatedArtworkTargetResolution > 0 ? config.AnimatedArtworkTargetResolution : null;
        }
        else if (string.Equals(quality.Trim(), "original", StringComparison.OrdinalIgnoreCase))
        {
            targetEdge = null;
        }
        else if (int.TryParse(quality, out var requestedEdge) && requestedEdge > 0)
        {
            targetEdge = requestedEdge;
        }
        else
        {
            return BadRequest(new { Error = $"Unsupported quality '{quality}'. Use a short-edge pixel size or 'original'." });
        }

        // Store lookup first: the common blind-try case (no loop for this item) is then a single
        // indexed lookup + 404, with no library manager call. The item-visibility check only runs
        // when there is actually an asset to protect.
        var record = Resolve(store, itemId, canonicalImageType);
        if (record == null && canonicalFallback != null)
        {
            record = Resolve(store, itemId, canonicalFallback);
        }

        if (record == null)
        {
            return NotFound();
        }

        var accessError = CheckItemVisible(itemId);
        if (accessError != null)
        {
            return accessError;
        }

        // Always stamped, fallback or not, so a client can tell which shape it actually got without
        // having to compare the URL it asked for against the bytes it received.
        Response.Headers[ServedSlotHeader] = record.ImageType;

        // Masters are stored at full upload quality; the target short edge is quantized to a ladder
        // step and cut from the master once, then served as a plain file like the master itself.
        // Anything that can't be produced falls through to the master: a client gets more pixels
        // than it asked for, never an error.
        var step = ArtworkSizeLadder.Resolve(targetEdge, record.Width, record.Height);
        if (step != null)
        {
            var cache = HttpContext.RequestServices.GetRequiredService<ArtworkDerivativeCache>();
            var derivative = await cache
                .GetOrCreateAsync(store, record, step.Value, ArtworkSizeLadder.BitrateKbpsFor(step.Value), cancellationToken)
                .ConfigureAwait(false);

            if (derivative != null)
            {
                StampServedSize(derivative.Width, derivative.Height);
                var derivedTag = new EntityTagHeaderValue('"' + derivative.Sha256 + '"');
                return PhysicalFile(
                    derivative.FilePath,
                    derivative.ContentType,
                    derivative.CreatedAt,
                    derivedTag,
                    enableRangeProcessing: true);
            }
        }

        StampServedSize(record.Width, record.Height);
        var etag = new EntityTagHeaderValue('"' + record.Sha256 + '"');
        return PhysicalFile(record.FilePath, record.ContentType, record.UploadedAt, etag, enableRangeProcessing: true);
    }

    /// <summary>
    /// Lists the animated-artwork assets stored for an album or artist. Lets a companion app see what
    /// is already uploaded without fetching the binaries. The caller must be able to see the item.
    /// </summary>
    /// <param name="itemId">The album or artist id.</param>
    /// <returns>The stored assets' metadata.</returns>
    [HttpGet("{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult List([FromRoute] Guid itemId)
    {
        var store = Plugin.Instance?.AnimatedArtworkStore;
        var config = Plugin.Instance?.Configuration;
        if (store == null || config == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!config.AnimatedArtworkEnabled)
        {
            return NotFound();
        }

        var accessError = CheckItemVisible(itemId);
        if (accessError != null)
        {
            return accessError;
        }

        // AvailableQualities lets a client pick a size from what this master can actually be cut
        // down to, instead of hard-coding the ladder and discovering the limit by request. Each is a
        // short edge, so what a step yields depends on the master's shape.
        var items = store.GetForItem(itemId).Select(r => new
        {
            ImageType = r.ImageType,
            r.ContentType,
            r.ByteLength,
            r.DurationSeconds,
            r.Width,
            r.Height,
            r.MoovAtFront,
            Tag = r.Sha256,
            r.UploadedAt,
            AvailableQualities = ArtworkSizeLadder.AvailableFor(r.Width, r.Height)
        });

        return Ok(new { ItemId = itemId, Items = items });
    }

    // Stamps the served dimensions, or nothing at all when they are unknown — a client can then tell
    // "the server doesn't know" from a real size, which a zero would blur.
    private void StampServedSize(int? width, int? height)
    {
        if (width is not int w || height is not int h || w <= 0 || h <= 0)
        {
            return;
        }

        Response.Headers[ServedSizeHeader] = string.Create(CultureInfo.InvariantCulture, $"{w}x{h}");
    }

    // Looks up one slot, treating an index row whose file has gone missing as "not there".
    private static AnimatedArtworkRecord? Resolve(AnimatedArtworkStore store, Guid itemId, string imageType)
    {
        var record = store.Get(itemId, imageType);
        return record != null && System.IO.File.Exists(record.FilePath) ? record : null;
    }

    private static string? NormalizeImageType(string imageType)
    {
        if (string.IsNullOrWhiteSpace(imageType) || !AllowedImageTypes.TryGetValue(imageType, out var canonical))
        {
            return null;
        }

        return canonical;
    }

    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var media = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
        return AllowedContentTypes.TryGetValue(media, out var canonical) ? canonical : null;
    }

    // Copies the request body into the temp file. Returns the number of bytes written.
    private async Task<long> CopyToTempAsync(string tempPath, CancellationToken cancellationToken)
    {
        long total = 0;
        var buffer = new byte[81920];
        using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        int read;
        while ((read = await Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    // Runs one ffmpeg rewrite (re-encode or faststart remux) of the staged upload into a fresh temp
    // path and re-probes the output. On success the old staged file is deleted and the new path and
    // probe are returned; on any failure the original is kept and null is returned so the upload
    // falls back to storing what was received.
    private static async Task<(string Path, ArtworkProbeResult Probe)?> RewriteAsync(
        AnimatedArtworkStore store,
        ArtworkProbe probe,
        string sourcePath,
        Func<string, string, Task<bool>> rewrite,
        CancellationToken cancellationToken)
    {
        var destPath = store.NewTempPath();
        var swapped = false;
        try
        {
            if (!await rewrite(sourcePath, destPath).ConfigureAwait(false))
            {
                return null;
            }

            var reprobed = await probe.ProbeAsync(destPath, cancellationToken).ConfigureAwait(false);
            if (reprobed == null)
            {
                return null;
            }

            swapped = true;
            TryDeleteTemp(sourcePath);
            return (destPath, reprobed);
        }
        finally
        {
            if (!swapped)
            {
                TryDeleteTemp(destPath);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort; a stray temp file is harmless and will be overwritten by name reuse only never (guid).
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    // Resolves the item and confirms the caller may see it. Returns an error result to short-circuit
    // with, or null when access is allowed.
    private ActionResult? CheckItemVisible(Guid itemId)
    {
        var user = GetJellyfinUser();
        if (user == null)
        {
            return Forbid();
        }

        var libraryManager = HttpContext.RequestServices.GetRequiredService<ILibraryManager>();
        var item = libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return NotFound();
        }

        return item.IsVisible(user) ? null : Forbid();
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

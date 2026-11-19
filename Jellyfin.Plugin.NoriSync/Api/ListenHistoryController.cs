using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Mime;
using Jellyfin.Plugin.NoriSync.Models;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// A user's listening history, shared by all of their devices: each device uploads the listens
/// it made and reads the ones made on the others. Everything here is scoped to the calling user.
/// </summary>
[ApiController]
[Route("api/norisync/listens")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize]
public class ListenHistoryController : ControllerBase
{
    /// <summary>
    /// The most listens accepted in one upload.
    /// </summary>
    internal const int MaxUploadCount = 500;

    /// <summary>
    /// The most listens returned in one page.
    /// </summary>
    internal const int MaxPageSize = 1000;

    private const int MaxIdLength = 64;
    private const int MaxContextTypeLength = 32;
    private const int MaxContextIdLength = 128;

    // A listen stamped further ahead than this is a broken clock, not a listen.
    private static readonly TimeSpan _maxClockLead = TimeSpan.FromHours(24);

    /// <summary>
    /// Stores listens made on the calling device. Uploading a listen again changes nothing.
    /// </summary>
    /// <param name="body">The listens.</param>
    /// <returns>The store's epoch and how many listens were new.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult Upload([FromBody] ListenUploadRequest body)
    {
        var store = Plugin.Instance?.ListenStore;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!TryGetCaller(out var userId, out var deviceId, out var error))
        {
            return error!;
        }

        var listens = body?.Listens ?? Array.Empty<ListenDto>();
        if (listens.Count > MaxUploadCount)
        {
            return BadRequest(new { Error = $"At most {MaxUploadCount} listens per request." });
        }

        var latestPlayedAt = DateTimeOffset.UtcNow.Add(_maxClockLead).ToUnixTimeMilliseconds();
        var records = new List<ListenRecord>(listens.Count);
        foreach (var listen in listens)
        {
            if (!TryBuildRecord(listen, deviceId, latestPlayedAt, out var record))
            {
                return BadRequest(new { Error = "Each listen needs an Id, a GUID ItemId, a PlayedAt in Unix milliseconds and a ContextType." });
            }

            records.Add(record!);
        }

        var accepted = store.Add(userId, records);
        return Ok(new { store.Epoch, Accepted = accepted });
    }

    /// <summary>
    /// Returns the calling user's listens made on their other devices, after a cursor.
    /// </summary>
    /// <param name="since">The <c>NextSince</c> of the previous page; 0 to read from the start.</param>
    /// <param name="limit">The most listens to return.</param>
    /// <returns>A page of listens, oldest sequence first.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<ListensResponse> GetListens([FromQuery] long since = 0, [FromQuery] int limit = MaxPageSize)
    {
        var store = Plugin.Instance?.ListenStore;
        if (store == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!TryGetCaller(out var userId, out var deviceId, out var error))
        {
            return error!;
        }

        var page = store.GetSince(userId, deviceId, Math.Max(0, since), Math.Clamp(limit, 1, MaxPageSize));

        var listens = new List<ListenDto>(page.Listens.Count);
        foreach (var record in page.Listens)
        {
            listens.Add(new ListenDto
            {
                Id = record.ListenId,
                ItemId = record.ItemId.ToString("N", CultureInfo.InvariantCulture),
                PlayedAt = record.PlayedAt,
                ContextType = record.ContextType,
                ContextId = record.ContextId,
                DeviceId = record.DeviceId
            });
        }

        return new ListensResponse
        {
            Epoch = store.Epoch,
            Listens = listens,
            NextSince = page.NextSince,
            HasMore = page.HasMore
        };
    }

    /// <summary>
    /// Validates one uploaded listen and turns it into a record for <paramref name="deviceId"/>.
    /// </summary>
    /// <param name="listen">The listen as uploaded.</param>
    /// <param name="deviceId">The uploading device.</param>
    /// <param name="latestPlayedAt">The latest acceptable <c>PlayedAt</c>, in Unix milliseconds.</param>
    /// <param name="record">The record, when valid.</param>
    /// <returns><see langword="true"/> if the listen is acceptable.</returns>
    internal static bool TryBuildRecord(ListenDto? listen, string deviceId, long latestPlayedAt, out ListenRecord? record)
    {
        record = null;
        if (listen == null
            || string.IsNullOrWhiteSpace(listen.Id) || listen.Id.Length > MaxIdLength
            || !Guid.TryParse(listen.ItemId, out var itemId) || itemId == Guid.Empty
            || listen.PlayedAt <= 0 || listen.PlayedAt > latestPlayedAt
            || string.IsNullOrWhiteSpace(listen.ContextType) || listen.ContextType.Length > MaxContextTypeLength
            || (listen.ContextId != null && listen.ContextId.Length > MaxContextIdLength))
        {
            return false;
        }

        record = new ListenRecord(
            listen.Id,
            itemId,
            listen.PlayedAt,
            deviceId,
            listen.ContextType,
            string.IsNullOrWhiteSpace(listen.ContextId) ? null : listen.ContextId);
        return true;
    }

    private bool TryGetCaller(out Guid userId, out string deviceId, out ActionResult? error)
    {
        var identity = RequestIdentity.Resolve(User, Request);
        userId = identity.UserId;
        deviceId = identity.DeviceId ?? string.Empty;
        error = null;

        var userManager = HttpContext.RequestServices.GetRequiredService<IUserManager>();
        if (userId == Guid.Empty || userManager.GetUserById(userId) == null)
        {
            error = Forbid();
            return false;
        }

        // History is told apart by device: without one, a device would be sent its own listens back.
        if (deviceId.Length == 0 || deviceId.Length > MaxContextIdLength * 2)
        {
            error = BadRequest(new { Error = "The request carries no device id." });
            return false;
        }

        return true;
    }
}

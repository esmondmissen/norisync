using System;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.NoriSync.Api;

/// <summary>
/// SoulSync setup for the configuration form.
/// </summary>
[ApiController]
[Route("api/norisync/soulsync")]
[Authorize]
public class SoulSyncController : ControllerBase
{
    private readonly SoulSyncClient _client;
    private readonly AlbumBackends _backends;

    /// <summary>
    /// Initializes a new instance of the <see cref="SoulSyncController"/> class.
    /// </summary>
    /// <param name="client">The SoulSync client.</param>
    /// <param name="backends">The album backends (for the Lidarr-compatible API's probe).</param>
    public SoulSyncController(SoulSyncClient client, AlbumBackends backends)
    {
        _client = client;
        _backends = backends;
    }

    /// <summary>
    /// Tries a SoulSync URL and key from the request body, without saving them: songs (its own
    /// API, with an admin key) and albums (its Lidarr-compatible API) separately, and its
    /// profiles. Admin-only: it makes the server call the given URL.
    /// </summary>
    /// <param name="request">The URL and key to try.</param>
    /// <returns>The outcome; connection failures are a 200 with a status, not an error.</returns>
    [HttpPost("probe")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SoulSyncProbeResponse>> Probe([FromBody] SoulSyncProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = RequestCallers.Resolve(this);
        if (user == null || !RequestCallers.IsAdministrator(user))
        {
            return Forbid();
        }

        return await _client.ProbeAsync(request.BaseUrl, request.ApiKey, _backends.SoulSync.Client, HttpContext.RequestAborted).ConfigureAwait(false);
    }
}

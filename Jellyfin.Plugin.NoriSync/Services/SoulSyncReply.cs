using System.Net;
using System.Text.Json;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// One SoulSync <c>/api/v1</c> answer, its envelope (<c>{success, data, error:{code,message}}</c>)
/// unwrapped.
/// </summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Data">The envelope's <c>data</c>, or null when it had none or wasn't JSON.</param>
/// <param name="ErrorCode">The envelope's <c>error.code</c> (<c>INVALID_KEY</c>, <c>CONFLICT</c>…).</param>
/// <param name="ErrorMessage">The envelope's <c>error.message</c>.</param>
/// <param name="IsEnvelope">Whether the body was SoulSync's envelope at all (an HTML page from a proxy isn't).</param>
public sealed record SoulSyncReply(HttpStatusCode Status, JsonElement? Data, string? ErrorCode, string? ErrorMessage, bool IsEnvelope)
{
    /// <summary>
    /// Gets a value indicating whether the call succeeded (2xx).
    /// </summary>
    public bool IsSuccess => (int)Status is >= 200 and <= 299;
}

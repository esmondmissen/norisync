using System;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Where a <see cref="LidarrClient"/> sends: a base URI ending in a slash, and the API key.
/// </summary>
/// <param name="BaseUri">Base URI; <c>api/v1/…</c> resolves beneath it.</param>
/// <param name="ApiKey">Sent as <c>X-Api-Key</c>.</param>
internal sealed record LidarrConnection(Uri BaseUri, string ApiKey);

using System;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Where a <see cref="SoulSyncClient"/> sends.
/// </summary>
/// <param name="BaseUri">SoulSync's base URI, ending in a slash; <c>api/v1/…</c> resolves beneath it.</param>
/// <param name="ApiKey">Sent as <c>X-API-Key</c>.</param>
/// <param name="ProfileId">Sent as <c>X-Profile-Id</c>: whose wishlist a song lands on.</param>
internal sealed record SoulSyncConnection(Uri BaseUri, string ApiKey, int ProfileId);

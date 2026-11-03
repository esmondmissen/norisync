using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Response of <c>GET /api/norisync/playlists/cover-designs</c>: the designs of every playlist
/// the caller can see whose cover is still the designed one.
/// </summary>
public sealed class PlaylistCoverDesignsResponse
{
    /// <summary>
    /// Gets or sets the designs, ordered by playlist id.
    /// </summary>
    public IReadOnlyList<PlaylistCoverDesignDto> Designs { get; set; } = Array.Empty<PlaylistCoverDesignDto>();
}

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// The outcome of trying a SoulSync URL and key: songs (its <c>/api/v1</c>) and albums (its
/// Lidarr-compatible API) separately, since the second can be off while the first works.
/// </summary>
public sealed class SoulSyncProbeResponse
{
    /// <summary>
    /// Gets or sets how the check of SoulSync's own API came out. Songs can be requested only
    /// when this is <see cref="SoulSyncProbeStatus.Connected"/>.
    /// </summary>
    public SoulSyncProbeStatus Status { get; set; }

    /// <summary>
    /// Gets or sets what to tell the admin when <see cref="Status"/> isn't connected.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets SoulSync's profiles, for the "whose wishlist" choice.
    /// </summary>
    public IReadOnlyList<LidarrChoice> Profiles { get; set; } = Array.Empty<LidarrChoice>();

    /// <summary>
    /// Gets or sets a value indicating whether SoulSync's Lidarr-compatible API answered, so it
    /// can take album requests.
    /// </summary>
    public bool AlbumsAvailable { get; set; }

    /// <summary>
    /// Gets or sets why albums aren't available, when they aren't.
    /// </summary>
    public string? AlbumsMessage { get; set; }
}

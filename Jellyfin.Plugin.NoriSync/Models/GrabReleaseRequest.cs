using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Request body for grabbing a specific release candidate returned by an interactive search
/// (<c>GET /api/norisync/lidarr/albums/{mbid}/releases</c>). The client copies these two
/// fields verbatim from the candidate it wants to download.
/// </summary>
public class GrabReleaseRequest
{
    /// <summary>
    /// Gets or sets the release GUID from Lidarr's interactive search. This is an
    /// indexer-specific opaque string (often a URL), not a <see cref="System.Guid"/>.
    /// </summary>
    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    /// <summary>
    /// Gets or sets the id of the Lidarr indexer the release came from. Together with
    /// <see cref="Guid"/> this identifies the cached release Lidarr will grab.
    /// </summary>
    [JsonPropertyName("indexerId")]
    public int IndexerId { get; set; }
}

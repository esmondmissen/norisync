using System;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// One playlist's cover design, served only while the playlist still shows the image the design
/// was baked into.
/// </summary>
public sealed class PlaylistCoverDesignDto
{
    /// <summary>
    /// Gets or sets the playlist id.
    /// </summary>
    public Guid PlaylistId { get; set; }

    /// <summary>
    /// Gets or sets the cache tag of the primary image the design belongs to — the same value
    /// as <c>ImageTags.Primary</c> on the playlist's <c>/Items</c> row, so a client can tell
    /// which cover this design is the source of.
    /// </summary>
    public string ImageTag { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the design document (JSON) exactly as the client uploaded it.
    /// </summary>
    public string Design { get; set; } = string.Empty;
}

using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Represents a single tracked change to a library item.
/// </summary>
public class ChangeRecord
{
    /// <summary>
    /// Gets or sets the unique identifier of the changed item.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the name of the changed item.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the type of item that changed.
    /// </summary>
    public TrackedItemType ItemType { get; set; }

    /// <summary>
    /// Gets or sets the type of change that occurred.
    /// </summary>
    public ChangeType ChangeType { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when the change was recorded.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the album name (for tracks).
    /// </summary>
    public string? AlbumName { get; set; }

    /// <summary>
    /// Gets or sets the artist names associated with this item.
    /// </summary>
    public IReadOnlyList<string>? ArtistNames { get; set; }
}

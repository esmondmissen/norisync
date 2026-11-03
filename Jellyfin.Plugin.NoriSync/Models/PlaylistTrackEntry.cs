using System;
using MessagePack;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Represents a track entry within a playlist, including its position.
/// </summary>
[MessagePackObject]
public class PlaylistTrackEntry
{
    /// <summary>
    /// Gets or sets the track's unique identifier.
    /// </summary>
    [Key(0)]
    public Guid TrackId { get; set; }

    /// <summary>
    /// Gets or sets the track's position in the playlist (zero-based).
    /// </summary>
    [Key(1)]
    public int SortOrder { get; set; }
}

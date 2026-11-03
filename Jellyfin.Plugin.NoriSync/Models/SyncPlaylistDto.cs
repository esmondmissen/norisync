using System;
using System.Collections.Generic;
using MessagePack;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Represents an audio playlist in the sync response.
/// </summary>
[MessagePackObject]
public class SyncPlaylistDto
{
    /// <summary>
    /// Gets or sets the playlist's unique identifier.
    /// </summary>
    [Key(0)]
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the playlist name.
    /// </summary>
    [Key(1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the playlist description.
    /// </summary>
    [Key(2)]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the tracks in this playlist with their sort order.
    /// </summary>
    [Key(3)]
    public IReadOnlyList<PlaylistTrackEntry> Tracks { get; set; } = Array.Empty<PlaylistTrackEntry>();

    /// <summary>
    /// Gets or sets the user id of the playlist owner.
    /// </summary>
    [Key(4)]
    public Guid OwnerUserId { get; set; }

    /// <summary>
    /// Gets or sets the username of the playlist owner.
    /// </summary>
    [Key(5)]
    public string OwnerUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the playlist is owned by the user performing the sync.
    /// </summary>
    [Key(6)]
    public bool IsOwner { get; set; }
}

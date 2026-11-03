using System;
using System.Collections.Generic;
using MessagePack;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Paginated incremental response for the playlist changes endpoint. Carries the full
/// state of each changed playlist visible to the caller, plus tombstones for playlists
/// that were deleted or are no longer visible to the caller (e.g. unshared or made private).
/// </summary>
[MessagePackObject]
public class PlaylistChangesResponse
{
    /// <summary>
    /// Gets or sets the total number of changed playlists visible to the caller (before pagination).
    /// </summary>
    [Key(0)]
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the starting index of this page.
    /// </summary>
    [Key(1)]
    public int StartIndex { get; set; }

    /// <summary>
    /// Gets or sets the changed playlists in this page (added or modified, and visible to the caller).
    /// </summary>
    [Key(2)]
    public IReadOnlyList<SyncPlaylistDto> Playlists { get; set; } = Array.Empty<SyncPlaylistDto>();

    /// <summary>
    /// Gets or sets the ids of playlists the caller should drop locally: deleted on the server,
    /// or no longer visible to the caller. Not paginated; the full set is returned every page.
    /// </summary>
    [Key(3)]
    public IReadOnlyList<Guid> DeletedPlaylistIds { get; set; } = Array.Empty<Guid>();
}

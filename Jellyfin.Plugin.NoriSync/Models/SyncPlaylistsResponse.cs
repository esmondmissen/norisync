using System.Collections.Generic;
using MessagePack;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Paginated response for the playlist sync endpoint.
/// </summary>
[MessagePackObject]
public class SyncPlaylistsResponse
{
    /// <summary>
    /// Gets or sets the total number of audio playlists in the library.
    /// </summary>
    [Key(0)]
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the starting index of this page.
    /// </summary>
    [Key(1)]
    public int StartIndex { get; set; }

    /// <summary>
    /// Gets or sets the playlists in this page.
    /// </summary>
    [Key(2)]
    public required IReadOnlyList<SyncPlaylistDto> Playlists { get; set; }
}

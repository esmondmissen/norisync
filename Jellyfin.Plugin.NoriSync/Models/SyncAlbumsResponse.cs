using System;
using System.Collections.Generic;
using MessagePack;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Paginated response for the album sync endpoint.
/// </summary>
[MessagePackObject]
public class SyncAlbumsResponse
{
    /// <summary>
    /// Gets or sets the total number of albums in the library.
    /// </summary>
    [Key(0)]
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the starting index of this page.
    /// </summary>
    [Key(1)]
    public int StartIndex { get; set; }

    /// <summary>
    /// Gets or sets the albums in this page.
    /// </summary>
    [Key(2)]
    public required IReadOnlyList<SyncAlbumDto> Albums { get; set; }

    /// <summary>
    /// Gets or sets the artists referenced by albums and tracks in this page.
    /// </summary>
    [Key(3)]
    public required IReadOnlyList<SyncArtistDto> Artists { get; set; }

    /// <summary>
    /// Gets or sets the IDs of albums that were deleted (changes endpoint only).
    /// </summary>
    [Key(4)]
    public IReadOnlyList<Guid> DeletedAlbumIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the IDs of tracks that were deleted (changes endpoint only).
    /// </summary>
    [Key(5)]
    public IReadOnlyList<Guid> DeletedTrackIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the IDs of artists that were deleted (changes endpoint only).
    /// </summary>
    [Key(6)]
    public IReadOnlyList<Guid> DeletedArtistIds { get; set; } = Array.Empty<Guid>();
}

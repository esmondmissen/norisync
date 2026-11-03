using System;
using System.Collections.Generic;
using MessagePack;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Represents an album in the sync response.
/// </summary>
[MessagePackObject]
public class SyncAlbumDto
{
    /// <summary>
    /// Gets or sets the album's unique identifier.
    /// </summary>
    [Key(0)]
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the album name.
    /// </summary>
    [Key(1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release year.
    /// </summary>
    [Key(2)]
    public int? ReleaseYear { get; set; }

    /// <summary>
    /// Gets or sets the album description.
    /// </summary>
    [Key(3)]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the album artist IDs.
    /// </summary>
    [Key(4)]
    public IReadOnlyList<Guid> AlbumArtistIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the album genres.
    /// </summary>
    [Key(5)]
    public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the tracks in this album.
    /// </summary>
    [Key(6)]
    public IReadOnlyList<SyncTrackDto> Tracks { get; set; } = Array.Empty<SyncTrackDto>();

    /// <summary>
    /// Gets or sets the sort name for the album.
    /// </summary>
    [Key(7)]
    public string SortName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the date the album was added to the library.
    /// </summary>
    [Key(8)]
    public DateTime DateAdded { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="Tracks"/> is a partial delta
    /// (only tracks that changed since the client's cursor) rather than the full track
    /// list for the album. True only on <c>/changes</c> responses when the album itself
    /// was not added or modified at the album level. Clients should merge partial deltas
    /// into their local copy instead of replacing the track list.
    /// </summary>
    [Key(9)]
    public bool TracksArePartial { get; set; }

    /// <summary>
    /// Gets or sets the MusicBrainz release-group id, when available. Populated from the
    /// album's Jellyfin <c>ProviderIds["MusicBrainzReleaseGroup"]</c> entry. This is the
    /// canonical "album" identifier used by Lidarr (<c>foreignAlbumId</c>) and by the
    /// plugin's Lidarr/cover endpoints — lets clients pivot from the sync feed into
    /// <c>/api/norisync/lidarr/albums/{mbid}/tracks</c> and
    /// <c>/api/norisync/lidarr/covers/release-group/{rgid}</c> without a fuzzy match.
    /// </summary>
    [Key(10)]
    public string? MusicBrainzReleaseGroupId { get; set; }
}

using System;
using MessagePack;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Represents an artist in the sync response.
/// </summary>
[MessagePackObject]
public class SyncArtistDto
{
    /// <summary>
    /// Gets or sets the artist's unique identifier.
    /// </summary>
    [Key(0)]
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the artist name.
    /// </summary>
    [Key(1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the artist description.
    /// </summary>
    [Key(2)]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the sort name for the artist.
    /// </summary>
    [Key(3)]
    public string SortName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the MusicBrainz artist id, when available. Populated from the artist's
    /// Jellyfin <c>ProviderIds["MusicBrainzArtist"]</c> entry. Lets clients pivot from the
    /// sync feed into MusicBrainz/Lidarr lookups (cover art, discography, requests) without
    /// a fuzzy name match.
    /// </summary>
    [Key(4)]
    public string? MusicBrainzId { get; set; }
}

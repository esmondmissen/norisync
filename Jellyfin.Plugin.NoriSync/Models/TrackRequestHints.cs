namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// What the app already knows about a song it requests, from the MusicBrainz result it shows.
/// Every field is optional: a missing title or artist is read from MusicBrainz.
/// </summary>
public sealed class TrackRequestHints
{
    /// <summary>
    /// Gets or sets the song title.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the (first credited) artist.
    /// </summary>
    public string? Artist { get; set; }

    /// <summary>
    /// Gets or sets the album title.
    /// </summary>
    public string? Album { get; set; }

    /// <summary>
    /// Gets or sets the album's MusicBrainz release-group id.
    /// </summary>
    public string? ReleaseGroupMbid { get; set; }

    /// <summary>
    /// Gets or sets the album's type (album, single, ep, compilation).
    /// </summary>
    public string? AlbumType { get; set; }

    /// <summary>
    /// Gets or sets the song's length in milliseconds.
    /// </summary>
    public long? DurationMs { get; set; }
}

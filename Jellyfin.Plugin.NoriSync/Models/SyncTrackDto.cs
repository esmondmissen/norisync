using System;
using System.Collections.Generic;
using MessagePack;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Represents a track in the sync response.
/// </summary>
[MessagePackObject]
public class SyncTrackDto
{
    /// <summary>
    /// Gets or sets the track's unique identifier.
    /// </summary>
    [Key(0)]
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the track name.
    /// </summary>
    [Key(1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the track's artist IDs.
    /// </summary>
    [Key(2)]
    public IReadOnlyList<Guid> ArtistIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets a value indicating whether the current user has liked this track.
    /// </summary>
    [Key(3)]
    public bool Liked { get; set; }

    /// <summary>
    /// Gets or sets the track duration in ticks.
    /// </summary>
    [Key(4)]
    public long RuntimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the file container format (e.g. "flac", "mp3").
    /// </summary>
    [Key(5)]
    public string Container { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the audio sample rate in Hz.
    /// </summary>
    [Key(6)]
    public int? SampleRate { get; set; }

    /// <summary>
    /// Gets or sets the sort name for the track.
    /// </summary>
    [Key(7)]
    public string SortName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the track number within the disc.
    /// </summary>
    [Key(8)]
    public int? IndexNumber { get; set; }

    /// <summary>
    /// Gets or sets the disc number.
    /// </summary>
    [Key(9)]
    public int? ParentIndexNumber { get; set; }

    /// <summary>
    /// Gets or sets the calling user's play count for this track.
    /// </summary>
    [Key(10)]
    public int PlayCount { get; set; }

    /// <summary>
    /// Gets or sets the calling user's last played date for this track, in UTC.
    /// </summary>
    [Key(11)]
    public DateTime? LastPlayedDate { get; set; }

    /// <summary>
    /// Gets or sets the cache tag for the track's own Primary image. Non-null only when
    /// the track has an embedded/per-track cover that is distinct from its album's Primary
    /// image. Clients should render the per-track artwork (e.g. via
    /// <c>/Items/{Id}/Images/Primary?tag={CoverTag}</c>) when this is set, and fall back to
    /// the album's cover otherwise. The plugin de-duplicates server-side so that "fake
    /// albums" of singles surface distinct covers while ordinary albums whose tracks share
    /// the album art leave this field <c>null</c>.
    /// </summary>
    [Key(12)]
    public string? CoverTag { get; set; }

    /// <summary>
    /// Gets or sets the size of the track's media file in bytes, as recorded by Jellyfin's
    /// library scan. <c>null</c> when Jellyfin has no size on record for the item.
    /// </summary>
    [Key(13)]
    public long? FileSize { get; set; }

    /// <summary>
    /// Gets or sets the MusicBrainz recording MBID for this track, from the audio item's
    /// <c>MusicBrainzRecording</c> provider id (Picard's "MusicBrainz Track Id" tag / UFID).
    /// <c>null</c> when the file isn't MusicBrainz-tagged. This is the recording-level id
    /// (shared across releases of the same recording), NOT the release-track id
    /// (<c>MusicBrainzTrack</c>). Consumed by the DJ Radio library sync.
    /// </summary>
    [Key(14)]
    public string? MusicBrainzRecordingId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the track carries an <c>Explicit</c> tag in
    /// Jellyfin (matched case-insensitively). Only the flag is sent; the tag list itself is not.
    /// </summary>
    [Key(15)]
    public bool Explicit { get; set; }
}

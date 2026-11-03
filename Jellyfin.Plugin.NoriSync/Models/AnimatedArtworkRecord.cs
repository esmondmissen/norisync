using System;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Metadata for a single animated-artwork asset (a short looping video, typically MP4) attached to
/// a Jellyfin item. The binary itself lives as a file under the plugin data path; this record is the
/// index row that points at it and carries the properties the serve/discovery endpoints need.
/// Keyed by <see cref="ItemId"/> + <see cref="ImageType"/>.
/// </summary>
public sealed class AnimatedArtworkRecord
{
    /// <summary>
    /// Gets or sets the Jellyfin item id the artwork belongs to (a music album or artist).
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item kind this artwork is attached to, e.g. <c>"Album"</c> or <c>"Artist"</c>.
    /// Informational; the serve endpoint keys on <see cref="ItemId"/> + <see cref="ImageType"/>.
    /// </summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the image slot this asset fills, e.g. <c>"Primary"</c> or <c>"Backdrop"</c>.
    /// </summary>
    public string ImageType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the MIME type of the stored asset, e.g. <c>"video/mp4"</c>. Echoed back verbatim
    /// on the serve response so the client sees the exact container it uploaded.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the on-disk file name (relative to the artwork directory) backing this record.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute path to the backing file. Populated by the store on read; not a
    /// persisted column.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the asset size in bytes.
    /// </summary>
    public long ByteLength { get; set; }

    /// <summary>
    /// Gets or sets the asset duration in seconds, when known from probing.
    /// </summary>
    public double? DurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the video width in pixels, when known.
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// Gets or sets the video height in pixels, when known.
    /// </summary>
    public int? Height { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the MP4 <c>moov</c> atom precedes <c>mdat</c>
    /// ("faststart"). <see langword="false"/> means the file starts slowly when streamed
    /// progressively; <see langword="null"/> when undetermined or not an MP4.
    /// </summary>
    public bool? MoovAtFront { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 (lowercase hex) of the asset bytes. Doubles as the client-facing
    /// cache tag / ETag: it changes whenever the asset changes, so clients refetch only on change.
    /// </summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC time the asset was uploaded.
    /// </summary>
    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin user id that uploaded the asset, when known.
    /// </summary>
    public string? UploadedBy { get; set; }
}

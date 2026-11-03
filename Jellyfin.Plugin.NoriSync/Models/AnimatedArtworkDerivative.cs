using System;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A cached, downscaled rendition of an animated-artwork master, produced the first time a client
/// asks for that size and served as a static file from then on. Keyed by
/// <see cref="ItemId"/> + <see cref="ImageType"/> + <see cref="ShortEdge"/>, where the edge is a
/// fixed ladder step rather than an arbitrary request, so the cache stays bounded.
/// </summary>
public sealed class AnimatedArtworkDerivative
{
    /// <summary>
    /// Gets or sets the Jellyfin item id the artwork belongs to.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the image slot of the master this was derived from.
    /// </summary>
    public string ImageType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ladder step, in pixels, this rendition's short edge was scaled to.
    /// </summary>
    public int ShortEdge { get; set; }

    /// <summary>
    /// Gets or sets the MIME type of the rendition. Always <c>video/mp4</c>: the derivation
    /// re-encodes to H.264 MP4 whatever the master's container was.
    /// </summary>
    public string ContentType { get; set; } = "video/mp4";

    /// <summary>
    /// Gets or sets the on-disk file name, relative to the derivative directory.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the absolute path to the backing file. Populated on read; not a persisted column.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rendition size in bytes.
    /// </summary>
    public long ByteLength { get; set; }

    /// <summary>
    /// Gets or sets the rendition duration in seconds, when known.
    /// </summary>
    public double? DurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the rendition width in pixels, when known.
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// Gets or sets the rendition height in pixels, when known.
    /// </summary>
    public int? Height { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 (lowercase hex) of this rendition's bytes, used as its ETag. It is
    /// distinct from the master's, so a client caching one size is unaffected by another.
    /// </summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the master's SHA-256 at the time this rendition was produced. A mismatch means
    /// the master has been replaced and the rendition is stale, so it is rebuilt rather than served.
    /// </summary>
    public string SourceSha256 { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC time the rendition was produced.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}

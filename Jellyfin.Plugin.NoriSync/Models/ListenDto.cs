namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A listen on the wire, in both directions of <c>/api/norisync/listens</c>.
/// </summary>
public sealed class ListenDto
{
    /// <summary>
    /// Gets or sets the client's id for the listen (a UUID). Uploading the same id twice stores it once.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the id of the audio item played, as a GUID in any standard format.
    /// </summary>
    public string? ItemId { get; set; }

    /// <summary>
    /// Gets or sets when playback started, in Unix milliseconds on the listening device's clock.
    /// </summary>
    public long PlayedAt { get; set; }

    /// <summary>
    /// Gets or sets what the track was played from (<c>album</c>, <c>playlist</c>, <c>station</c>, …).
    /// </summary>
    public string? ContextType { get; set; }

    /// <summary>
    /// Gets or sets the id of the album / playlist / artist it was played from, when that is a
    /// server item; <see langword="null"/> otherwise.
    /// </summary>
    public string? ContextId { get; set; }

    /// <summary>
    /// Gets or sets the device that played it. Set by the server on responses; ignored on upload
    /// (the caller's own device id is used).
    /// </summary>
    public string? DeviceId { get; set; }
}

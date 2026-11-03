using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// The type of library item being tracked.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackedItemType
{
    /// <summary>
    /// An audio track (song).
    /// </summary>
    Track,

    /// <summary>
    /// A music album.
    /// </summary>
    Album,

    /// <summary>
    /// A music artist.
    /// </summary>
    Artist,

    /// <summary>
    /// A playlist (audio only).
    /// </summary>
    Playlist
}

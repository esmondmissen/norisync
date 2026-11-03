using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// The type of change that occurred.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeType
{
    /// <summary>
    /// An item was added to the library.
    /// </summary>
    Added,

    /// <summary>
    /// An existing item was modified.
    /// </summary>
    Modified,

    /// <summary>
    /// An item was removed from the library.
    /// </summary>
    Deleted
}

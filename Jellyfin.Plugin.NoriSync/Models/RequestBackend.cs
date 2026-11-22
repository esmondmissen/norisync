using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Which service an album request went to. Album ids are that service's own, so the same id
/// can name two different albums on the two backends.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequestBackend
{
    /// <summary>
    /// Lidarr. Absent on the wire means this one: every request before SoulSync was.
    /// </summary>
    Lidarr,

    /// <summary>
    /// SoulSync, through its Lidarr-compatible API (<c>{SoulSync}/lidarr/api/v1</c>).
    /// </summary>
    SoulSync,
}

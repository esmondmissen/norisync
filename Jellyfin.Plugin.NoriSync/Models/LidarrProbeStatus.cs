using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Outcome of a Lidarr connection probe.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LidarrProbeStatus
{
    /// <summary>
    /// Lidarr answered and every list was read.
    /// </summary>
    Connected,

    /// <summary>
    /// The URL is empty or not an absolute http(s) URL.
    /// </summary>
    InvalidUrl,

    /// <summary>
    /// No API key was given.
    /// </summary>
    MissingApiKey,

    /// <summary>
    /// The server could not open a connection to the URL (DNS, refused, TLS, ...).
    /// </summary>
    Unreachable,

    /// <summary>
    /// The URL accepted the connection but did not answer in time.
    /// </summary>
    TimedOut,

    /// <summary>
    /// Lidarr answered 401 or 403: the API key is wrong.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// Something answered, but it is not the Lidarr v1 API (another *arr app, a login page, ...).
    /// </summary>
    NotLidarr,

    /// <summary>
    /// Lidarr answered with an error status or a body that could not be read.
    /// </summary>
    UnexpectedResponse,
}

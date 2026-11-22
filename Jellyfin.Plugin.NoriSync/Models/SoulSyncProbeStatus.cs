using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// How a SoulSync connection check came out.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SoulSyncProbeStatus
{
    /// <summary>
    /// SoulSync answered and the key is an admin key.
    /// </summary>
    Connected,

    /// <summary>
    /// The URL isn't a full http(s) URL.
    /// </summary>
    InvalidUrl,

    /// <summary>
    /// No API key was entered.
    /// </summary>
    MissingApiKey,

    /// <summary>
    /// The server couldn't connect to the URL.
    /// </summary>
    Unreachable,

    /// <summary>
    /// The URL didn't answer in time.
    /// </summary>
    TimedOut,

    /// <summary>
    /// SoulSync refused the key.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// The key is SoulSync's read-only kind, which can't add anything.
    /// </summary>
    ReadOnlyKey,

    /// <summary>
    /// Something answered, but not SoulSync's API.
    /// </summary>
    NotSoulSync,

    /// <summary>
    /// SoulSync answered with something unexpected.
    /// </summary>
    UnexpectedResponse,
}

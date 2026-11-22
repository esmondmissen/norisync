using System;
using System.Net;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Lidarr answered a call with an error status.
/// </summary>
public sealed class LidarrUpstreamException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LidarrUpstreamException"/> class.
    /// </summary>
    /// <param name="status">Lidarr's status code.</param>
    /// <param name="body">Lidarr's response body (for logs; never passed to clients).</param>
    public LidarrUpstreamException(HttpStatusCode status, string? body)
        : base("Lidarr answered " + (int)status)
    {
        Status = status;
        Body = body;
    }

    /// <summary>
    /// Gets Lidarr's status code.
    /// </summary>
    public HttpStatusCode Status { get; }

    /// <summary>
    /// Gets Lidarr's response body.
    /// </summary>
    public string? Body { get; }
}

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Body of <c>POST /api/norisync/lidarr/probe</c>: a Lidarr URL and API key to try, typically
/// still unsaved in the plugin configuration form.
/// </summary>
public sealed class LidarrProbeRequest
{
    /// <summary>
    /// Gets or sets the Lidarr base URL to try, such as <c>http://lidarr:8686</c>.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the Lidarr API key to try.
    /// </summary>
    public string? ApiKey { get; set; }
}

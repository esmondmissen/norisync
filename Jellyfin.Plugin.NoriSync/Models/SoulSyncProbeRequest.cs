namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A SoulSync URL and key to try, from the configuration form, before they are saved.
/// </summary>
public sealed class SoulSyncProbeRequest
{
    /// <summary>
    /// Gets or sets the SoulSync base URL.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the API key.
    /// </summary>
    public string? ApiKey { get; set; }
}

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// One entry of a Lidarr list offered as a dropdown option (a root folder or a profile).
/// </summary>
public sealed class LidarrChoice
{
    /// <summary>
    /// Gets or sets the value stored in plugin configuration: the folder path, or the profile id
    /// as a string.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text shown to the admin.
    /// </summary>
    public string Label { get; set; } = string.Empty;
}

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// The root folder and profiles the plugin adds artists with (its configuration).
/// </summary>
/// <param name="RootFolderPath">Lidarr root folder.</param>
/// <param name="QualityProfileId">Quality profile id.</param>
/// <param name="MetadataProfileId">Metadata profile id.</param>
public sealed record LidarrAddDefaults(string? RootFolderPath, int QualityProfileId, int MetadataProfileId)
{
    /// <summary>
    /// Gets a value indicating whether all three are set.
    /// </summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(RootFolderPath) && QualityProfileId > 0 && MetadataProfileId > 0;
}

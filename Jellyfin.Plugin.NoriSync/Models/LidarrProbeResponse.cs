using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Response of <c>POST /api/norisync/lidarr/probe</c>. Every probe outcome, including a failed
/// connection, is a 200 with <see cref="Status"/> set; the lists are only filled when connected.
/// </summary>
public sealed class LidarrProbeResponse
{
    /// <summary>
    /// Gets or sets the probe outcome.
    /// </summary>
    public LidarrProbeStatus Status { get; set; }

    /// <summary>
    /// Gets or sets a short explanation for the admin when <see cref="Status"/> is not
    /// <see cref="LidarrProbeStatus.Connected"/>.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the Lidarr version reported by <c>system/status</c>, when known.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets Lidarr's root folders.
    /// </summary>
    public IReadOnlyList<LidarrChoice> RootFolders { get; set; } = Array.Empty<LidarrChoice>();

    /// <summary>
    /// Gets or sets Lidarr's quality profiles.
    /// </summary>
    public IReadOnlyList<LidarrChoice> QualityProfiles { get; set; } = Array.Empty<LidarrChoice>();

    /// <summary>
    /// Gets or sets Lidarr's metadata profiles.
    /// </summary>
    public IReadOnlyList<LidarrChoice> MetadataProfiles { get; set; } = Array.Empty<LidarrChoice>();
}

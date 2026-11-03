using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Response model for the changes API endpoint.
/// </summary>
public class ChangesResponse
{
    /// <summary>
    /// Gets or sets the list of change records.
    /// </summary>
    public IReadOnlyList<ChangeRecord> Changes { get; set; } = Array.Empty<ChangeRecord>();

    /// <summary>
    /// Gets or sets the total number of changes returned.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the most recent change, or null if no changes exist.
    /// </summary>
    public DateTimeOffset? LatestTimestamp { get; set; }
}

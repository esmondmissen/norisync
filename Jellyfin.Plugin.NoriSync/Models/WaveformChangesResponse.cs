using System;
using System.Collections.Generic;
using MessagePack;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Paginated response for the waveform changes feed. Ordered ascending by
/// <see cref="WaveformChangeDto.ProcessedAt"/>.
/// </summary>
[MessagePackObject]
public class WaveformChangesResponse
{
    /// <summary>
    /// Gets or sets the total number of records matching the query (pre-pagination).
    /// </summary>
    [Key(0)]
    public long TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the zero-based index of the first record in this page.
    /// </summary>
    [Key(1)]
    public int StartIndex { get; set; }

    /// <summary>
    /// Gets or sets the records on this page.
    /// </summary>
    [Key(2)]
    public IReadOnlyList<WaveformChangeDto> Items { get; set; } = Array.Empty<WaveformChangeDto>();

    /// <summary>
    /// Gets or sets the <see cref="WaveformChangeDto.ProcessedAt"/> of the last record on this
    /// page. Clients can pass this to the next request as <c>since</c> to advance the cursor.
    /// </summary>
    [Key(3)]
    public DateTimeOffset? LatestProcessedAt { get; set; }
}

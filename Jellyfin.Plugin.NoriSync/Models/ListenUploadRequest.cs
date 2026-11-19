using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Request body for <c>POST /api/norisync/listens</c>.
/// </summary>
public sealed class ListenUploadRequest
{
    /// <summary>
    /// Gets or sets the listens to store.
    /// </summary>
    public IReadOnlyList<ListenDto> Listens { get; set; } = Array.Empty<ListenDto>();
}

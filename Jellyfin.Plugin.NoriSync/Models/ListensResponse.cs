using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Response of <c>GET /api/norisync/listens</c>: a page of the caller's listens made on their
/// other devices.
/// </summary>
public sealed class ListensResponse
{
    /// <summary>
    /// Gets or sets the identity of the listen store. It changes only when the store is lost or
    /// reset; a client that sees a new value re-uploads its own listens and reads from 0 again.
    /// </summary>
    public string Epoch { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the listens in this page, oldest sequence first.
    /// </summary>
    public IReadOnlyList<ListenDto> Listens { get; set; } = Array.Empty<ListenDto>();

    /// <summary>
    /// Gets or sets the cursor to pass as <c>since</c> next time.
    /// </summary>
    public long NextSince { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether more listens are waiting past this page.
    /// </summary>
    public bool HasMore { get; set; }
}

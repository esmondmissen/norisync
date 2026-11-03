namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// Request body for <c>POST /api/norisync/dj/library</c>. The body is optional; an absent body is
/// equivalent to one with no proposal.
/// </summary>
public sealed class DjLibraryClaimRequest
{
    /// <summary>
    /// Gets or sets a DJ library id the client already holds (for example one it minted locally
    /// before the server became the source of truth), offered for adoption. The server adopts it
    /// only when the calling user has no id yet and no other user already owns this one; otherwise
    /// it is ignored. <see langword="null"/> or blank means no proposal. A non-blank value must be a
    /// non-empty GUID.
    /// </summary>
    public string? ProposedLibraryId { get; set; }
}

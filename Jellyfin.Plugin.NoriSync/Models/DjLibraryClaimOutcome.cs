namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// How a <see cref="Services.DjLibraryStore.Claim"/> call arrived at the DJ library id it returned.
/// </summary>
public enum DjLibraryClaimOutcome
{
    /// <summary>
    /// The user already had a DJ library id on this server; it was returned unchanged and any
    /// proposal from the client was ignored.
    /// </summary>
    Existing,

    /// <summary>
    /// The user had no DJ library id yet and the client's proposed id was adopted. This is the
    /// migration path for a client that minted an id locally before the server owned it, so the
    /// library it already uploaded under that id keeps working.
    /// </summary>
    Adopted,

    /// <summary>
    /// The user had no DJ library id yet and the server minted a fresh one, either because the
    /// client proposed none or because its proposal was empty or already belongs to another user.
    /// </summary>
    Minted
}

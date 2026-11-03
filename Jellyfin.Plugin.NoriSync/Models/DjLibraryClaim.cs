using System;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// The result of claiming a user's DJ library id: the id itself, which is a bearer credential for
/// the external DJ service and must be treated as a secret, and how it was arrived at.
/// </summary>
/// <param name="LibraryId">The user's DJ library id on this server.</param>
/// <param name="Outcome">Whether the id was already stored, adopted from the client's proposal, or freshly minted.</param>
public readonly record struct DjLibraryClaim(Guid LibraryId, DjLibraryClaimOutcome Outcome);

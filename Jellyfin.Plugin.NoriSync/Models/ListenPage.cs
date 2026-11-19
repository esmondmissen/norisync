using System.Collections.Generic;

namespace Jellyfin.Plugin.NoriSync.Models;

/// <summary>
/// A page of a user's listen feed.
/// </summary>
/// <param name="Listens">The listens, oldest sequence first.</param>
/// <param name="NextSince">The cursor to pass as <c>since</c> for the next page.</param>
/// <param name="HasMore">Whether more listens were waiting past this page.</param>
public sealed record ListenPage(IReadOnlyList<ListenRecord> Listens, long NextSince, bool HasMore);

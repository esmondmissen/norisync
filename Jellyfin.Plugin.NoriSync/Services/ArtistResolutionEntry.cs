using System;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// A name-to-id mapping an <see cref="ArtistNameLookup"/> resolves by, as recorded between runs.
/// </summary>
/// <param name="IsCleanName">Whether <paramref name="Name"/> is a clean name rather than an
/// exact (case-insensitive) one.</param>
/// <param name="Name">The name.</param>
/// <param name="ArtistId">The artist it resolves to.</param>
public readonly record struct ArtistResolutionEntry(bool IsCleanName, string Name, Guid ArtistId);

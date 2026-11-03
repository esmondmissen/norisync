using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Resolves the artist name strings carried on albums and tracks to <c>MusicArtist</c> ids.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin links a tag to an artist by its <em>clean</em> name (12.x
/// <c>LinkedChildrenService.FindArtists</c>, which the server's own DTOs use), not the raw
/// string. An exact match alone silently drops every credit whose punctuation differs from the
/// artist record: MusicBrainz names carry U+2010 HYPHEN ("Anne‐Marie") while file tags carry
/// ASCII "-", so albums tagged that way reached clients with no artist at all — shown as
/// Unknown Artist, and the artist missing from the Artists list once nothing referenced her.
/// </para>
/// <para>
/// An exact (case-insensitive) match still wins, so every name that resolved before keeps the
/// id it had; the clean name is only consulted on a miss. Where several artists share a name
/// (the same artist with a folder in two libraries, say) the filesystem artist is preferred over
/// an ItemsByName one, as in <c>LibraryManager.GetArtist</c>, then the lowest id — for exact and
/// clean matches alike — so every request and every user gets the same id for a name.
/// </para>
/// </remarks>
public sealed class ArtistNameLookup
{
    /// <summary>
    /// The memory-cache key the sync endpoints memoise the lookup under. Evicted when an artist
    /// changes, so a memo built before it can't serve credits that now resolve elsewhere.
    /// </summary>
    public const string CacheKey = "norisync:artistlookup";

    private static readonly Regex NonWordCharacters = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly Dictionary<string, Candidate> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Candidate> _clean = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _cleanNameCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _names = new();

    private ArtistNameLookup()
    {
    }

    /// <summary>
    /// Builds the lookup from every <c>MusicArtist</c> on the server.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>The lookup.</returns>
    public static ArtistNameLookup FromLibrary(ILibraryManager libraryManager)
    {
        var artistItems = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
            Recursive = true
        });

        var artists = new List<(string Name, Guid Id, bool IsAccessedByName)>(artistItems.Count);
        foreach (var item in artistItems)
        {
            if (item is MusicArtist artist)
            {
                artists.Add((artist.Name, artist.Id, artist.IsAccessedByName));
            }
        }

        return Build(artists);
    }

    /// <summary>
    /// Builds the lookup from artist records.
    /// </summary>
    /// <param name="artists">Each artist's name, id, and whether it is an ItemsByName artist
    /// (no folder of its own).</param>
    /// <returns>The lookup.</returns>
    public static ArtistNameLookup Build(IEnumerable<(string Name, Guid Id, bool IsAccessedByName)> artists)
    {
        var lookup = new ArtistNameLookup();
        foreach (var (name, id, isAccessedByName) in artists)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!lookup._names.TryAdd(id, name))
            {
                continue;
            }

            var candidate = new Candidate(id, isAccessedByName);
            AddPreferred(lookup._exact, name, candidate);

            var clean = GetCleanName(name);
            if (string.IsNullOrEmpty(clean))
            {
                continue;
            }

            AddPreferred(lookup._clean, clean, candidate);
            lookup._cleanNameCounts[clean] = lookup._cleanNameCounts.GetValueOrDefault(clean) + 1;
        }

        return lookup;
    }

    /// <summary>
    /// Jellyfin 12.x's <c>StringExtensions.GetCleanValue</c>, which the 10.11 packages this
    /// plugin builds against don't expose: diacritics removed, lower-cased, every character that
    /// is not a letter, digit or whitespace turned into a space, whitespace collapsed and trimmed.
    /// </summary>
    /// <param name="value">The name.</param>
    /// <returns>The clean name.</returns>
    public static string GetCleanName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value.RemoveDiacritics().ToLowerInvariant();
        cleaned = NonWordCharacters.Replace(cleaned, " ");
        return Whitespace.Replace(cleaned, " ").Trim();
    }

    /// <summary>
    /// Resolves a name to an artist id.
    /// </summary>
    /// <param name="name">The artist name as carried on an album or track.</param>
    /// <param name="artistId">The resolved id.</param>
    /// <returns>Whether the name resolved.</returns>
    public bool TryResolve(string name, out Guid artistId)
    {
        artistId = Guid.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (_exact.TryGetValue(name, out var candidate) || _clean.TryGetValue(GetCleanName(name), out candidate))
        {
            artistId = candidate.Id;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether another artist shares this artist's clean name — so which of them a credit
    /// resolves to rests on the tie-break, not the name alone.
    /// </summary>
    /// <param name="artistId">An artist id this lookup resolved.</param>
    /// <returns>Whether the artist has a namesake.</returns>
    public bool HasNamesake(Guid artistId)
        => _names.TryGetValue(artistId, out var name)
            && _cleanNameCounts.GetValueOrDefault(GetCleanName(name)) > 1;

    /// <summary>
    /// The name-to-id mappings this lookup resolves by, for recording so a later lookup can be
    /// compared against it with <see cref="GetChangedCredits"/>.
    /// </summary>
    /// <returns>The entries.</returns>
    public IReadOnlyList<ArtistResolutionEntry> GetEntries()
    {
        var entries = new List<ArtistResolutionEntry>(_exact.Count + _clean.Count);
        foreach (var (name, candidate) in _exact)
        {
            entries.Add(new ArtistResolutionEntry(false, name, candidate.Id));
        }

        foreach (var (name, candidate) in _clean)
        {
            entries.Add(new ArtistResolutionEntry(true, name, candidate.Id));
        }

        return entries;
    }

    /// <summary>
    /// Compares this lookup against an earlier one's recorded entries.
    /// </summary>
    /// <remarks>
    /// A credit resolves by its exact entry, else by its clean-name entry, so it can only resolve
    /// differently when one of those two entries was added, removed, or re-pointed. Matching on
    /// either is conservative — a credit whose exact entry held still is matched when only its
    /// clean entry moved — which re-sends a little more than needed, never less.
    /// </remarks>
    /// <param name="previous">The entries recorded from the earlier lookup.</param>
    /// <returns>A filter matching every credit that may resolve differently now, or
    /// <see langword="null"/> when none can.</returns>
    public Func<string, bool>? GetChangedCredits(IEnumerable<ArtistResolutionEntry> previous)
    {
        var oldExact = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var oldClean = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var entry in previous)
        {
            (entry.IsCleanName ? oldClean : oldExact)[entry.Name] = entry.ArtistId;
        }

        var changedExact = GetChangedNames(oldExact, _exact, StringComparer.OrdinalIgnoreCase);
        var changedClean = GetChangedNames(oldClean, _clean, StringComparer.Ordinal);
        if (changedExact.Count == 0 && changedClean.Count == 0)
        {
            return null;
        }

        return name => !string.IsNullOrEmpty(name)
            && (changedExact.Contains(name) || changedClean.Contains(GetCleanName(name)));
    }

    private static HashSet<string> GetChangedNames(
        Dictionary<string, Guid> before,
        Dictionary<string, Candidate> after,
        StringComparer comparer)
    {
        var changed = new HashSet<string>(comparer);
        foreach (var (name, id) in before)
        {
            if (!after.TryGetValue(name, out var candidate) || candidate.Id != id)
            {
                changed.Add(name);
            }
        }

        foreach (var name in after.Keys)
        {
            if (!before.ContainsKey(name))
            {
                changed.Add(name);
            }
        }

        return changed;
    }

    private static void AddPreferred(Dictionary<string, Candidate> map, string key, Candidate candidate)
    {
        if (!map.TryGetValue(key, out var existing) || candidate.IsPreferredOver(existing))
        {
            map[key] = candidate;
        }
    }

    /// <summary>
    /// Resolves a name to an artist id, also giving the artist's own spelling of its name — which
    /// differs from <paramref name="name"/> when the credit is cased or punctuated differently.
    /// </summary>
    /// <param name="name">The artist name as carried on an album or track.</param>
    /// <param name="artistId">The resolved id.</param>
    /// <param name="artistName">The resolved artist's name.</param>
    /// <returns>Whether the name resolved.</returns>
    public bool TryResolve(string name, out Guid artistId, out string artistName)
    {
        artistName = string.Empty;
        if (!TryResolve(name, out artistId))
        {
            return false;
        }

        artistName = _names[artistId];
        return true;
    }

    private readonly record struct Candidate(Guid Id, bool IsAccessedByName)
    {
        public bool IsPreferredOver(Candidate other)
        {
            if (IsAccessedByName != other.IsAccessedByName)
            {
                return !IsAccessedByName;
            }

            return Id.CompareTo(other.Id) < 0;
        }
    }
}

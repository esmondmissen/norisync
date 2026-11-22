using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// The one rule for "is this the requested song" by name, for a library item and a SoulSync
/// download alike: SoulSync's download tasks carry no wishlist id, and a file may be tagged with
/// another release's recording id.
/// </summary>
public static class TrackMatch
{
    /// <summary>
    /// Whether a title and artists name the requested song: the same title, letters and digits
    /// only, case ignored; and an artist that is the requested one or contains it either way
    /// ("Daft Punk" in "Daft Punk, Pharrell Williams").
    /// </summary>
    /// <param name="title">The candidate's title.</param>
    /// <param name="artists">The candidate's artists.</param>
    /// <param name="wantedTitle">The requested title.</param>
    /// <param name="wantedArtist">The requested artist.</param>
    /// <returns>True when they match.</returns>
    public static bool SameSong(string? title, IEnumerable<string>? artists, string wantedTitle, string wantedArtist)
    {
        var t = Fold(title);
        if (t.Length == 0 || t != Fold(wantedTitle))
        {
            return false;
        }

        var wanted = Fold(wantedArtist);
        return wanted.Length > 0 && (artists ?? Enumerable.Empty<string>())
            .Select(Fold)
            .Any(a => a.Length > 0 && (a.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(a, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Letters and digits only, lower-case.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The folded text.</returns>
    internal static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}

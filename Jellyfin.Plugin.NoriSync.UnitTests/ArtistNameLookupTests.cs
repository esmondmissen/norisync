using System;
using Jellyfin.Plugin.NoriSync.Services;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Tests for <see cref="ArtistNameLookup"/>: an album or track credit whose punctuation differs
/// from the artist record must still resolve, the way Jellyfin itself links it, while every name
/// that already matched exactly keeps the id it had.
/// </summary>
public sealed class ArtistNameLookupTests
{
    private static readonly Guid AnneMarie = Guid.Parse("fadc7922-fed4-edb0-1ad3-aebd1bf1d4f2");

    [Theory]
    [InlineData("Björk", "bjork")]
    [InlineData("AC/DC", "ac dc")]
    [InlineData(" AN   ARTIST ", "an artist")]
    [InlineData("Anne‐Marie", "anne marie")]   // U+2010 HYPHEN, as MusicBrainz writes it
    [InlineData("Anne-Marie", "anne marie")]
    [InlineData("P!nk", "p nk")]
    [InlineData("", "")]
    public void GetCleanName_MatchesJellyfinsCleanValue(string name, string expected)
        => Assert.Equal(expected, ArtistNameLookup.GetCleanName(name));

    [Fact]
    public void TagWithAsciiHyphenResolvesToTheMusicBrainzNamedArtist()
    {
        var lookup = ArtistNameLookup.Build(new[] { ("Anne‐Marie", AnneMarie, false) });

        Assert.True(lookup.TryResolve("Anne-Marie", out var id, out var artistName));
        Assert.Equal(AnneMarie, id);
        Assert.Equal("Anne‐Marie", artistName);
    }

    [Fact]
    public void CaseVariantResolvesExactlyAndReportsTheArtistsOwnSpelling()
    {
        var lookup = ArtistNameLookup.Build(new[] { ("Anne‐Marie", AnneMarie, false) });

        Assert.True(lookup.TryResolve("anne‐marie", out var id, out var artistName));
        Assert.Equal(AnneMarie, id);
        Assert.Equal("Anne‐Marie", artistName);
    }

    [Fact]
    public void UnresolvedNameReportsNoSpelling()
    {
        var lookup = ArtistNameLookup.Build(new[] { ("Anne‐Marie", AnneMarie, false) });

        Assert.False(lookup.TryResolve("Someone Else", out _, out var artistName));
        Assert.Equal(string.Empty, artistName);
    }

    [Fact]
    public void ExactMatchWinsOverAnotherArtistWithTheSameCleanName()
    {
        var folderArtist = Guid.NewGuid();
        var byNameArtist = Guid.NewGuid();
        var lookup = ArtistNameLookup.Build(new[]
        {
            ("AC/DC", folderArtist, false),
            ("AC DC", byNameArtist, true)
        });

        Assert.True(lookup.TryResolve("AC DC", out var id));
        Assert.Equal(byNameArtist, id);
    }

    [Fact]
    public void CleanNameCollisionPrefersTheFilesystemArtist()
    {
        var byNameArtist = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var folderArtist = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var lookup = ArtistNameLookup.Build(new[]
        {
            ("Bjork", byNameArtist, true),
            ("Björk", folderArtist, false)
        });

        Assert.True(lookup.TryResolve("BJÖRK!", out var id));
        Assert.Equal(folderArtist, id);
    }

    [Fact]
    public void CleanNameCollisionBetweenEqualsIsStable()
    {
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

        var forward = ArtistNameLookup.Build(new[] { ("Ne‐Yo", high, false), ("Ne.Yo", low, false) });
        var reverse = ArtistNameLookup.Build(new[] { ("Ne.Yo", low, false), ("Ne‐Yo", high, false) });

        Assert.True(forward.TryResolve("Ne-Yo", out var a));
        Assert.True(reverse.TryResolve("Ne-Yo", out var b));
        Assert.Equal(low, a);
        Assert.Equal(low, b);
    }

    [Fact]
    public void SameNamedArtistsResolveTheSameWayWhateverTheOrder()
    {
        var byName = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var musicFolder = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var hiResFolder = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var artists = new[]
        {
            ("Nirvana", byName, true),
            ("Nirvana", musicFolder, false),
            ("nirvana", hiResFolder, false)
        };

        var forward = ArtistNameLookup.Build(artists);
        var reverse = ArtistNameLookup.Build(new[] { artists[2], artists[1], artists[0] });

        Assert.True(forward.TryResolve("Nirvana", out var a));
        Assert.True(reverse.TryResolve("Nirvana", out var b));
        Assert.Equal(hiResFolder, a);
        Assert.Equal(hiResFolder, b);
    }

    [Fact]
    public void HasNamesakeOnlyWhenAnotherArtistSharesTheCleanName()
    {
        var hyphen = Guid.NewGuid();
        var ascii = Guid.NewGuid();
        var alone = Guid.NewGuid();
        var lookup = ArtistNameLookup.Build(new[]
        {
            ("Anne‐Marie", hyphen, false),
            ("Anne-Marie", ascii, true),
            ("Björk", alone, false)
        });

        Assert.True(lookup.HasNamesake(hyphen));
        Assert.True(lookup.HasNamesake(ascii));
        Assert.False(lookup.HasNamesake(alone));
        Assert.False(lookup.HasNamesake(Guid.NewGuid()));
    }

    [Fact]
    public void UnchangedArtistsReportNoChangedCredits()
    {
        var artists = new[] { ("Anne‐Marie", AnneMarie, false), ("Björk", Guid.NewGuid(), false) };

        var before = ArtistNameLookup.Build(artists);
        var after = ArtistNameLookup.Build(new[] { artists[1], artists[0] });

        Assert.Null(after.GetChangedCredits(before.GetEntries()));
    }

    [Fact]
    public void DeletingADuplicateFlagsEveryCreditOfThatName()
    {
        // 10.11 left a case-only duplicate the plugin resolved both spellings to; the merge
        // migration deletes it, so both spellings now reach the keeper.
        var duplicate = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var keeper = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var before = ArtistNameLookup.Build(new[]
        {
            ("Thirty Seconds To Mars", duplicate, false),
            ("Thirty Seconds to Mars", keeper, false)
        });
        var after = ArtistNameLookup.Build(new[] { ("Thirty Seconds to Mars", keeper, false) });

        var changed = after.GetChangedCredits(before.GetEntries());

        Assert.NotNull(changed);
        Assert.True(changed!("Thirty Seconds To Mars"));
        Assert.True(changed("Thirty Seconds to Mars"));
        Assert.True(changed("Thirty Seconds to Mars!"));
        Assert.False(changed("Björk"));
    }

    [Fact]
    public void SweepingTheExactMatchFlagsTheCreditNowResolvedByCleanName()
    {
        var ascii = Guid.NewGuid();
        var before = ArtistNameLookup.Build(new[] { ("Anne‐Marie", AnneMarie, false), ("Anne-Marie", ascii, true) });
        var after = ArtistNameLookup.Build(new[] { ("Anne‐Marie", AnneMarie, false) });

        var changed = after.GetChangedCredits(before.GetEntries());

        Assert.NotNull(changed);
        Assert.True(changed!("Anne-Marie"));
        Assert.True(after.TryResolve("Anne-Marie", out var id));
        Assert.Equal(AnneMarie, id);
    }

    [Fact]
    public void ANewArtistFlagsCreditsThatResolvedToNothing()
    {
        var before = ArtistNameLookup.Build(new[] { ("Björk", Guid.NewGuid(), false) });
        var after = ArtistNameLookup.Build(new[] { ("Björk", Guid.NewGuid(), false), ("Anne‐Marie", AnneMarie, false) });

        var changed = after.GetChangedCredits(before.GetEntries());

        Assert.NotNull(changed);
        Assert.True(changed!("Anne-Marie"));
        Assert.True(changed("Björk"));
        Assert.False(changed("Someone Else"));
    }

    [Fact]
    public void ARecordedExactNameMatchesWhateverItsCasing()
    {
        var id = Guid.NewGuid();
        var before = ArtistNameLookup.Build(new[] { ("NIRVANA", id, false) });
        var after = ArtistNameLookup.Build(new[] { ("Nirvana", id, false) });

        Assert.Null(after.GetChangedCredits(before.GetEntries()));
    }

    [Theory]
    [InlineData("Anne Marie Smith")]
    [InlineData("Anne")]
    [InlineData("")]
    [InlineData("---")]
    public void DifferentNamesDoNotResolve(string name)
    {
        var lookup = ArtistNameLookup.Build(new[] { ("Anne‐Marie", AnneMarie, false), ("!!!", Guid.NewGuid(), false) });

        Assert.False(lookup.TryResolve(name, out _));
    }
}

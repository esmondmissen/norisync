using System;
using System.Reflection;
using Jellyfin.Plugin.NoriSync.Api;
using Jellyfin.Plugin.NoriSync.Models;
using MessagePack;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Guards the per-track <c>Explicit</c> flag: the server reads Jellyfin's tag list and sends a
/// single boolean, never the tags themselves.
/// </summary>
public class ExplicitFlagTests
{
    [Theory]
    [InlineData("Explicit")]
    [InlineData("explicit")]
    [InlineData("EXPLICIT")]
    [InlineData("eXpLiCiT")]
    [InlineData(" Explicit ")]
    public void IsExplicit_MatchesTagCaseInsensitively(string tag)
    {
        Assert.True(ChangesController.IsExplicit(new[] { tag }));
    }

    [Fact]
    public void IsExplicit_FindsTagAmongOthers()
    {
        Assert.True(ChangesController.IsExplicit(new[] { "Live", "explicit", "Remaster" }));
    }

    [Theory]
    [InlineData("Explicit Content")]
    [InlineData("NotExplicit")]
    [InlineData("Clean")]
    [InlineData("")]
    public void IsExplicit_RejectsNonExactTags(string tag)
    {
        Assert.False(ChangesController.IsExplicit(new[] { tag }));
    }

    [Fact]
    public void IsExplicit_FalseForNullOrEmptyTagList()
    {
        Assert.False(ChangesController.IsExplicit(null));
        Assert.False(ChangesController.IsExplicit(Array.Empty<string>()));
        Assert.False(ChangesController.IsExplicit(new string[] { null! }));
    }

    [Fact]
    public void SyncTrackDto_ExplicitIsPinnedToKey15()
    {
        var keyAttr = typeof(SyncTrackDto).GetProperty(nameof(SyncTrackDto.Explicit))!.GetCustomAttribute<KeyAttribute>();

        Assert.NotNull(keyAttr);
        Assert.Equal(15, keyAttr!.IntKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SyncTrackDto_ExplicitRoundTripsThroughMessagePack(bool isExplicit)
    {
        var dto = new SyncTrackDto { Id = Guid.NewGuid(), Name = "Track", Explicit = isExplicit };

        var roundTripped = MessagePackSerializer.Deserialize<SyncTrackDto>(MessagePackSerializer.Serialize(dto));

        Assert.Equal(isExplicit, roundTripped.Explicit);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.NoriSync.Models;
using MessagePack;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Wire-format guardrails for <see cref="AudioInfoDto"/>. Pins the MessagePack key layout so a
/// future refactor that reshuffles a <c>[Key]</c> fails here instead of silently breaking clients.
/// </summary>
public class AudioInfoDtoTests
{
    [Fact]
    public void AudioInfoDto_KeyAttributes_PinnedToCurrentWireLayout()
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Codec"] = 0,
            ["SampleRate"] = 1,
            ["Channels"] = 2,
            ["TotalFrames"] = 3,
            ["TotalFramesExact"] = 4,
            ["EncoderDelay"] = 5,
            ["EncoderPadding"] = 6,
            ["MoovAtFront"] = 7,
            ["DurationTicks"] = 8
        };

        var actual = typeof(AudioInfoDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(
                p => p.Name,
                p =>
                {
                    var keyAttr = p.GetCustomAttribute<KeyAttribute>();
                    Assert.NotNull(keyAttr);
                    return keyAttr!.IntKey ?? throw new InvalidOperationException(
                        $"AudioInfoDto.{p.Name} has a non-integer Key attribute");
                });

        Assert.Equal(expected.Count, actual.Count);
        foreach (var (name, index) in expected)
        {
            Assert.True(actual.ContainsKey(name), $"AudioInfoDto is missing property '{name}'");
            Assert.Equal(index, actual[name]);
        }
    }

    [Fact]
    public void AudioInfoDto_RoundTripsViaMessagePack()
    {
        var dto = new AudioInfoDto
        {
            Codec = "aac",
            SampleRate = 44100,
            Channels = 2,
            TotalFrames = 11997240,
            TotalFramesExact = true,
            EncoderDelay = 2112,
            EncoderPadding = 364,
            MoovAtFront = true,
            DurationTicks = 2_720_000_000
        };

        var bytes = MessagePackSerializer.Serialize(dto);
        var roundTripped = MessagePackSerializer.Deserialize<AudioInfoDto>(bytes);

        Assert.Equal(dto.Codec, roundTripped.Codec);
        Assert.Equal(dto.SampleRate, roundTripped.SampleRate);
        Assert.Equal(dto.Channels, roundTripped.Channels);
        Assert.Equal(dto.TotalFrames, roundTripped.TotalFrames);
        Assert.Equal(dto.TotalFramesExact, roundTripped.TotalFramesExact);
        Assert.Equal(dto.EncoderDelay, roundTripped.EncoderDelay);
        Assert.Equal(dto.EncoderPadding, roundTripped.EncoderPadding);
        Assert.Equal(dto.MoovAtFront, roundTripped.MoovAtFront);
        Assert.Equal(dto.DurationTicks, roundTripped.DurationTicks);
    }

    [Fact]
    public void AudioInfoDto_PreservesNullableNulls()
    {
        var dto = new AudioInfoDto
        {
            Codec = "flac",
            SampleRate = 96000,
            Channels = 2,
            TotalFrames = 9000000,
            TotalFramesExact = true,
            EncoderDelay = 0,
            EncoderPadding = 0,
            MoovAtFront = null,
            DurationTicks = 0
        };

        var bytes = MessagePackSerializer.Serialize(dto);
        var roundTripped = MessagePackSerializer.Deserialize<AudioInfoDto>(bytes);

        Assert.Null(roundTripped.MoovAtFront);
        Assert.Equal(0, roundTripped.EncoderDelay);
    }
}

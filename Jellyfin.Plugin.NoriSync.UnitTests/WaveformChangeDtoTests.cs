using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.NoriSync.Models;
using MessagePack;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Wire-format guardrails for the waveform changes feed DTOs. The embedding-removal redesign
/// renumbered <see cref="WaveformChangeDto"/> in a clean break; these tests pin the new
/// layout so any future refactor that accidentally re-shuffles a <c>[Key]</c> attribute
/// fails immediately instead of silently corrupting client decoders.
/// </summary>
public class WaveformChangeDtoTests
{
    [Fact]
    public void WaveformChangeDto_KeyAttributes_PinnedToCurrentWireLayout()
    {
        // If you intentionally add a field, add it here with its new key index and update the
        // count check. If you intentionally renumber, that is a breaking wire change — bump
        // the protocol version in API_FORMAT.md and update both this test and any client.
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ItemId"] = 0,
            ["ProcessedAt"] = 1,
            ["SourceMtime"] = 2,
            ["Waveform"] = 3,
            ["WaveformVersion"] = 4
        };

        AssertKeyLayout(typeof(WaveformChangeDto), expected);
    }

    [Fact]
    public void WaveformChangesResponse_KeyAttributes_PinnedToCurrentWireLayout()
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["TotalCount"] = 0,
            ["StartIndex"] = 1,
            ["Items"] = 2,
            ["LatestProcessedAt"] = 3
        };

        AssertKeyLayout(typeof(WaveformChangesResponse), expected);
    }

    [Fact]
    public void WaveformChangeDto_RoundTripsViaMessagePack()
    {
        var dto = new WaveformChangeDto
        {
            ItemId = Guid.NewGuid(),
            ProcessedAt = new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero),
            SourceMtime = 12345678,
            Waveform = new byte[] { 1, 2, 3 },
            WaveformVersion = "waveform-v5.6"
        };

        var bytes = MessagePackSerializer.Serialize(dto);
        var roundTripped = MessagePackSerializer.Deserialize<WaveformChangeDto>(bytes);

        Assert.Equal(dto.ItemId, roundTripped.ItemId);
        Assert.Equal(dto.ProcessedAt, roundTripped.ProcessedAt);
        Assert.Equal(dto.SourceMtime, roundTripped.SourceMtime);
        Assert.Equal(dto.Waveform, roundTripped.Waveform);
        Assert.Equal(dto.WaveformVersion, roundTripped.WaveformVersion);
    }

    [Fact]
    public void WaveformChangeDto_RoundTripsRowWithNullWaveform()
    {
        // A row whose analyzer failed goes on the wire with Waveform=null and a null version;
        // the MessagePack contract must preserve null exactly so clients can detect it.
        var dto = new WaveformChangeDto
        {
            ItemId = Guid.NewGuid(),
            ProcessedAt = DateTimeOffset.UtcNow,
            SourceMtime = 100,
            Waveform = null,
            WaveformVersion = null
        };

        var bytes = MessagePackSerializer.Serialize(dto);
        var roundTripped = MessagePackSerializer.Deserialize<WaveformChangeDto>(bytes);

        Assert.Null(roundTripped.Waveform);
        Assert.Null(roundTripped.WaveformVersion);
        Assert.Equal(dto.SourceMtime, roundTripped.SourceMtime);
    }

    private static void AssertKeyLayout(Type dtoType, IReadOnlyDictionary<string, int> expected)
    {
        var actual = dtoType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(
                p => p.Name,
                p =>
                {
                    var keyAttr = p.GetCustomAttribute<KeyAttribute>();
                    Assert.NotNull(keyAttr);
                    return keyAttr!.IntKey ?? throw new InvalidOperationException(
                        $"{dtoType.Name}.{p.Name} has a non-integer Key attribute");
                });

        Assert.Equal(expected.Count, actual.Count);
        foreach (var (name, index) in expected)
        {
            Assert.True(actual.ContainsKey(name), $"{dtoType.Name} is missing property '{name}'");
            Assert.Equal(index, actual[name]);
        }
    }
}

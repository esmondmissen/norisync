using System;
using Jellyfin.Plugin.NoriSync.Services;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Tests for the in-place 60 Hz → 30 Hz legacy waveform conversion. The merge must OR the
/// kick/onset flags and take the per-band max of the 4-bit magnitudes, exactly mirroring the
/// analyzer's downsample pass.
/// </summary>
public class WaveformAnalyzerTests
{
    private static readonly byte[] V5Header = { 0x4C, 0x54, 0x05, 0x00 };

    [Fact]
    public void ConvertLegacyToCurrent_MergesFramePairs()
    {
        // Frame A: kick set, no onsets, bands [1,2,3,4,5].
        var a = PackWord(kick: true, onsets: new[] { false, false, false, false, false }, bands: new[] { 1, 2, 3, 4, 5 });
        // Frame B: no kick, onset on band 2, bands [5,1,2,9,0].
        var b = PackWord(kick: false, onsets: new[] { false, false, true, false, false }, bands: new[] { 5, 1, 2, 9, 0 });

        var blob = BuildV5Blob(a, b);
        var v6 = WaveformAnalyzer.ConvertLegacyToCurrent(blob);

        Assert.NotNull(v6);
        // v6 magic header + exactly one merged frame.
        Assert.Equal(WaveformAnalyzer.MagicHeader, v6![..4]);
        Assert.Equal(WaveformAnalyzer.MagicHeader.Length + 4, v6.Length);

        var merged = ReadWord(v6, 4);
        // kick = OR(1,0) = 1; onset band 2 = OR(0,1) = 1; bands = elementwise max.
        var expected = PackWord(kick: true, onsets: new[] { false, false, true, false, false }, bands: new[] { 5, 2, 3, 9, 5 });
        Assert.Equal(expected, merged);
    }

    [Fact]
    public void ConvertLegacyToCurrent_OddFrameCount_PreservesFinalFrameUnchanged()
    {
        var f0 = PackWord(kick: true, onsets: new[] { false, false, false, false, false }, bands: new[] { 1, 1, 1, 1, 1 });
        var f1 = PackWord(kick: false, onsets: new[] { false, false, false, false, false }, bands: new[] { 2, 2, 2, 2, 2 });
        var f2 = PackWord(kick: false, onsets: new[] { true, false, false, false, false }, bands: new[] { 7, 0, 0, 0, 0 });

        var blob = BuildV5Blob(f0, f1, f2);
        var v6 = WaveformAnalyzer.ConvertLegacyToCurrent(blob);

        Assert.NotNull(v6);
        Assert.Equal(WaveformAnalyzer.MagicHeader.Length + (2 * 4), v6!.Length);
        // The lone tail frame survives a single-frame window unchanged.
        Assert.Equal(f2, ReadWord(v6, 4 + 4));
    }

    [Fact]
    public void ConvertLegacyToCurrent_EmptyV5Blob_ReturnsBareV6Header()
    {
        var v6 = WaveformAnalyzer.ConvertLegacyToCurrent(V5Header);

        Assert.NotNull(v6);
        Assert.Equal(WaveformAnalyzer.MagicHeader, v6);
    }

    [Theory]
    [InlineData(new byte[] { 0x4C, 0x54, 0x06, 0x00, 0, 0, 0, 0 })] // already v6
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })] // unknown magic
    [InlineData(new byte[] { 0x4C })] // too short
    public void ConvertLegacyToCurrent_NonV5Input_ReturnsNull(byte[] blob)
    {
        Assert.Null(WaveformAnalyzer.ConvertLegacyToCurrent(blob));
    }

    [Fact]
    public void ConvertLegacyToCurrent_Null_ReturnsNull()
    {
        Assert.Null(WaveformAnalyzer.ConvertLegacyToCurrent(null));
    }

    private static uint PackWord(bool kick, bool[] onsets, int[] bands)
    {
        var word = kick ? 1u << 31 : 0u;
        for (var i = 0; i < 5; i++)
        {
            if (onsets[i])
            {
                word |= 1u << (30 - i);
            }
        }

        word |= (uint)bands[0] << 22;
        word |= (uint)bands[1] << 18;
        word |= (uint)bands[2] << 14;
        word |= (uint)bands[3] << 10;
        word |= (uint)bands[4] << 6;
        return word;
    }

    private static byte[] BuildV5Blob(params uint[] frames)
    {
        var blob = new byte[V5Header.Length + (frames.Length * 4)];
        Buffer.BlockCopy(V5Header, 0, blob, 0, V5Header.Length);
        var idx = V5Header.Length;
        foreach (var f in frames)
        {
            blob[idx++] = (byte)(f >> 24);
            blob[idx++] = (byte)(f >> 16);
            blob[idx++] = (byte)(f >> 8);
            blob[idx++] = (byte)(f & 0xFF);
        }

        return blob;
    }

    private static uint ReadWord(byte[] data, int offset) =>
        ((uint)data[offset] << 24)
        | ((uint)data[offset + 1] << 16)
        | ((uint)data[offset + 2] << 8)
        | data[offset + 3];
}

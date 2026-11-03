using System;
using System.IO;
using System.Text;
using Jellyfin.Plugin.NoriSync.Services;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Exercises the container-specific parsers that <see cref="AudioInfoProbe"/> uses to recover the
/// exact playable sample count and encoder delay/padding. These are pure functions over byte/string
/// fixtures and need no Jellyfin host or ffprobe binary.
/// </summary>
public class AudioInfoProbeTests
{
    [Fact]
    public void TryParseITunSmpb_ExtractsDelayPaddingAndFrames()
    {
        // Real-world iTunSMPB shape: 12 space-separated hex fields, leading space included.
        // Field 1 = encoder delay, field 2 = padding, field 3 = original sample count.
        const string Tag = " 00000000 00000840 0000016C 0000000000B71238 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000";

        var ok = AudioInfoProbe.TryParseITunSmpb(Tag, out var delay, out var padding, out var frames);

        Assert.True(ok);
        Assert.Equal(0x840, delay);     // 2112
        Assert.Equal(0x16C, padding);   // 364
        Assert.Equal(0xB71238L, frames); // 11997240
    }

    [Theory]
    [InlineData("")]
    [InlineData("not hex at all")]
    [InlineData(" 00000000 00000840")] // too few fields
    public void TryParseITunSmpb_RejectsMalformed(string tag)
    {
        Assert.False(AudioInfoProbe.TryParseITunSmpb(tag, out _, out _, out _));
    }

    [Fact]
    public void TryParseMp3_Mpeg1Stereo_ComputesExactFrames()
    {
        const int FrameCount = 10000;
        const int Delay = 576;
        const int Padding = 1800;

        var bytes = BuildMp3(frameCount: FrameCount, delay: Delay, padding: Padding, mono: false, mpeg1: true, id3Padding: 0);
        using var stream = new MemoryStream(bytes);

        var ok = AudioInfoProbe.TryParseMp3(stream, out var totalFrames, out var delay, out var padding);

        Assert.True(ok);
        Assert.Equal(Delay, delay);
        Assert.Equal(Padding, padding);
        // 1152 samples/frame for MPEG-1 Layer III, minus delay and padding.
        Assert.Equal(((long)FrameCount * 1152) - Delay - Padding, totalFrames);
    }

    [Fact]
    public void TryParseMp3_SkipsId3v2Tag()
    {
        var bytes = BuildMp3(frameCount: 500, delay: 1105, padding: 288, mono: true, mpeg1: true, id3Padding: 100);
        using var stream = new MemoryStream(bytes);

        var ok = AudioInfoProbe.TryParseMp3(stream, out var totalFrames, out var delay, out var padding);

        Assert.True(ok);
        Assert.Equal(1105, delay);
        Assert.Equal(288, padding);
        Assert.Equal(((long)500 * 1152) - 1105 - 288, totalFrames);
    }

    [Fact]
    public void TryParseMp3_NoXingHeader_ReturnsFalse()
    {
        // A bare frame header with no Xing/Info tag at the expected offset.
        var bytes = new byte[64];
        bytes[0] = 0xFF;
        bytes[1] = 0xFB; // MPEG-1 Layer III
        using var stream = new MemoryStream(bytes);

        Assert.False(AudioInfoProbe.TryParseMp3(stream, out _, out _, out _));
    }

    [Fact]
    public void DetectMoovAtFront_ReturnsTrue_WhenMoovBeforeMdat()
    {
        var bytes = BuildMp4(moovFirst: true);
        using var stream = new MemoryStream(bytes);

        Assert.True(AudioInfoProbe.DetectMoovAtFront(stream));
    }

    [Fact]
    public void DetectMoovAtFront_ReturnsFalse_WhenMdatBeforeMoov()
    {
        var bytes = BuildMp4(moovFirst: false);
        using var stream = new MemoryStream(bytes);

        Assert.False(AudioInfoProbe.DetectMoovAtFront(stream));
    }

    private static byte[] BuildMp3(int frameCount, int delay, int padding, bool mono, bool mpeg1, int id3Padding)
    {
        using var ms = new MemoryStream();

        if (id3Padding > 0)
        {
            // Minimal ID3v2 header with a syncsafe size, followed by id3Padding filler bytes.
            ms.Write(Encoding.ASCII.GetBytes("ID3"));
            ms.WriteByte(0x03); // version major
            ms.WriteByte(0x00); // version minor
            ms.WriteByte(0x00); // flags (no footer)
            ms.WriteByte((byte)((id3Padding >> 21) & 0x7F));
            ms.WriteByte((byte)((id3Padding >> 14) & 0x7F));
            ms.WriteByte((byte)((id3Padding >> 7) & 0x7F));
            ms.WriteByte((byte)(id3Padding & 0x7F));
            ms.Write(new byte[id3Padding]);
        }

        var frame = new byte[256];
        frame[0] = 0xFF;
        // byte[1]: version bits 3/4 (11 = MPEG1, 10 = MPEG2), layer bits (01 = Layer III).
        frame[1] = (byte)(mpeg1 ? 0xFB : 0xF3);
        frame[2] = 0x90;
        // byte[3] bits 7-6: channel mode. 11 = mono, 00 = stereo.
        frame[3] = (byte)(mono ? 0xC0 : 0x00);

        var xingOffset = mpeg1 ? (mono ? 21 : 36) : (mono ? 13 : 21);
        Encoding.ASCII.GetBytes("Xing").CopyTo(frame, xingOffset);

        var flagsOffset = xingOffset + 4;
        frame[flagsOffset + 3] = 0x01; // frames-present flag only

        var frameCountOffset = flagsOffset + 4;
        frame[frameCountOffset] = (byte)((frameCount >> 24) & 0xFF);
        frame[frameCountOffset + 1] = (byte)((frameCount >> 16) & 0xFF);
        frame[frameCountOffset + 2] = (byte)((frameCount >> 8) & 0xFF);
        frame[frameCountOffset + 3] = (byte)(frameCount & 0xFF);

        // LAME tag begins right after the 4-byte frame count; delay/padding sit 21 bytes in.
        var lameOffset = frameCountOffset + 4;
        Encoding.ASCII.GetBytes("LAME3.99r").CopyTo(frame, lameOffset);

        var delayOffset = lameOffset + 21;
        frame[delayOffset] = (byte)((delay >> 4) & 0xFF);
        frame[delayOffset + 1] = (byte)(((delay & 0x0F) << 4) | ((padding >> 8) & 0x0F));
        frame[delayOffset + 2] = (byte)(padding & 0xFF);

        ms.Write(frame);
        return ms.ToArray();
    }

    private static byte[] BuildMp4(bool moovFirst)
    {
        using var ms = new MemoryStream();
        WriteBox(ms, "ftyp", 16);
        if (moovFirst)
        {
            WriteBox(ms, "moov", 8);
            WriteBox(ms, "mdat", 16);
        }
        else
        {
            WriteBox(ms, "mdat", 16);
            WriteBox(ms, "moov", 8);
        }

        return ms.ToArray();
    }

    private static void WriteBox(Stream stream, string type, int size)
    {
        stream.WriteByte((byte)((size >> 24) & 0xFF));
        stream.WriteByte((byte)((size >> 16) & 0xFF));
        stream.WriteByte((byte)((size >> 8) & 0xFF));
        stream.WriteByte((byte)(size & 0xFF));
        stream.Write(Encoding.ASCII.GetBytes(type));
        if (size > 8)
        {
            stream.Write(new byte[size - 8]);
        }
    }
}

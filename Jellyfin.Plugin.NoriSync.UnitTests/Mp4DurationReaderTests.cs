using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Jellyfin.Plugin.NoriSync.Services;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// Tests for <see cref="Mp4DurationReader"/>, which reads MP4/MOV duration from the box structure.
/// Covers the case that motivated it — a fragmented MP4 whose <c>moov</c> declares no duration, so
/// the length must be summed from the movie fragments — plus the progressive header case and
/// non-MP4 input.
/// </summary>
public sealed class Mp4DurationReaderTests
{
    /// <summary>
    /// A fragmented MP4 (empty moov duration) must be measured from the summed fragment sample
    /// durations: 3 samples of 500 units at a 1000 Hz media timescale = 1.5 seconds.
    /// </summary>
    [Fact]
    public void FragmentedMp4_DurationSummedFromFragments()
    {
        var file = BuildMp4(
            movieTimescale: 1000,
            movieDuration: 0,
            mediaTimescale: 1000,
            mediaDuration: 0,
            fragmentSampleDurations: new uint[] { 500, 500, 500 });

        var seconds = RunOnTempFile(file);

        Assert.NotNull(seconds);
        Assert.Equal(1.5, seconds!.Value, precision: 3);
    }

    /// <summary>
    /// A progressive MP4 must be measured from the track header: duration 3000 at a 1000 Hz media
    /// timescale = 3.0 seconds, with no fragments present.
    /// </summary>
    [Fact]
    public void ProgressiveMp4_DurationFromHeader()
    {
        var file = BuildMp4(
            movieTimescale: 1000,
            movieDuration: 3000,
            mediaTimescale: 1000,
            mediaDuration: 3000,
            fragmentSampleDurations: null);

        var seconds = RunOnTempFile(file);

        Assert.NotNull(seconds);
        Assert.Equal(3.0, seconds!.Value, precision: 3);
    }

    /// <summary>
    /// A file that is not an MP4 must return null rather than throwing.
    /// </summary>
    [Fact]
    public void NonMp4_ReturnsNull()
    {
        var seconds = RunOnTempFile(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        Assert.Null(seconds);
    }

    private static double? RunOnTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "norisync-mp4-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(path, bytes);
        try
        {
            return Mp4DurationReader.TryReadDurationSeconds(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildMp4(
        uint movieTimescale,
        ulong movieDuration,
        uint mediaTimescale,
        ulong mediaDuration,
        uint[]? fragmentSampleDurations)
    {
        const uint trackId = 1;

        var mvhd = Box("mvhd", Concat(new byte[12], U32(movieTimescale), U32((uint)movieDuration)));
        var mdhd = Box("mdhd", Concat(new byte[12], U32(mediaTimescale), U32((uint)mediaDuration)));
        var hdlr = Box("hdlr", Concat(new byte[8], Ascii("vide")));
        var mdia = Box("mdia", Concat(mdhd, hdlr));
        var tkhd = Box("tkhd", Concat(new byte[12], U32(trackId)));
        var trak = Box("trak", Concat(tkhd, mdia));
        var trex = Box("trex", Concat(new byte[4], U32(trackId), U32(0), U32(0), U32(0), U32(0)));
        var mvex = Box("mvex", trex);
        var moov = Box("moov", Concat(mvhd, trak, mvex));

        var ftyp = Box("ftyp", Concat(Ascii("isom"), U32(0), Ascii("isom")));

        if (fragmentSampleDurations == null)
        {
            return Concat(ftyp, moov);
        }

        var tfhd = Box("tfhd", Concat(U32(0), U32(trackId)));

        // trun with sample-duration-present (0x000100) and one 4-byte duration per sample.
        var trunParts = new List<byte[]> { U32(0x000100), U32((uint)fragmentSampleDurations.Length) };
        foreach (var d in fragmentSampleDurations)
        {
            trunParts.Add(U32(d));
        }

        var trun = Box("trun", Concat(trunParts.ToArray()));
        var traf = Box("traf", Concat(tfhd, trun));
        var moof = Box("moof", traf);

        return Concat(ftyp, moov, moof);
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var buffer = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)buffer.Length);
        Ascii(type).CopyTo(buffer, 4);
        payload.CopyTo(buffer, 8);
        return buffer;
    }

    private static byte[] U32(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        return buffer;
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static byte[] Concat(params byte[][] parts)
    {
        var length = 0;
        foreach (var part in parts)
        {
            length += part.Length;
        }

        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}

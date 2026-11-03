using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Reads the video duration of an MP4/MOV file directly from its box structure, without trusting
/// ffprobe's <c>format.duration</c> — which is only a bitrate estimate for fragmented MP4 files
/// whose <c>moov</c> header carries no duration. Handles both progressive MP4 (duration in the
/// track <c>mdhd</c>/movie <c>mvhd</c>) and fragmented MP4 (duration summed from the
/// <c>moof</c> → <c>traf</c> → <c>trun</c> per-sample durations).
/// </summary>
internal static class Mp4DurationReader
{
    // Guards against reading an absurdly large "leaf" box into memory from a malformed file.
    // Real mvhd/mdhd/trex/tfhd are tiny; a trun for a 30s clip is a few tens of KB.
    private const long MaxLeafBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Attempts to read the video duration, in seconds, from an MP4/MOV file.
    /// </summary>
    /// <param name="path">Absolute path of the file.</param>
    /// <returns>The duration in seconds, or <see langword="null"/> if it cannot be determined.</returns>
    public static double? TryReadDurationSeconds(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var state = new ParseState();
            ParseTop(stream, stream.Length, state);
            return Resolve(state);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static void ParseTop(Stream stream, long end, ParseState state)
    {
        while (stream.Position < end && TryReadHeader(stream, end, out var type, out var payloadStart, out var payloadEnd))
        {
            if (type == "moov")
            {
                ParseMoov(stream, payloadStart, payloadEnd, state);
            }
            else if (type == "moof")
            {
                ParseMoof(stream, payloadStart, payloadEnd, state);
            }

            stream.Position = payloadEnd;
        }
    }

    private static void ParseMoov(Stream stream, long start, long end, ParseState state)
    {
        stream.Position = start;
        while (stream.Position < end && TryReadHeader(stream, end, out var type, out var payloadStart, out var payloadEnd))
        {
            switch (type)
            {
                case "mvhd":
                    ReadMvhd(stream, payloadStart, payloadEnd, state);
                    break;
                case "trak":
                    ParseTrak(stream, payloadStart, payloadEnd, state);
                    break;
                case "mvex":
                    ParseMvex(stream, payloadStart, payloadEnd, state);
                    break;
            }

            stream.Position = payloadEnd;
        }
    }

    private static void ParseTrak(Stream stream, long start, long end, ParseState state)
    {
        stream.Position = start;
        var trak = new TrakInfo();
        while (stream.Position < end && TryReadHeader(stream, end, out var type, out var payloadStart, out var payloadEnd))
        {
            switch (type)
            {
                case "tkhd":
                    ReadTkhd(stream, payloadStart, payloadEnd, trak);
                    break;
                case "mdia":
                    ParseMdia(stream, payloadStart, payloadEnd, trak);
                    break;
            }

            stream.Position = payloadEnd;
        }

        state.Traks.Add(trak);
    }

    private static void ParseMdia(Stream stream, long start, long end, TrakInfo trak)
    {
        stream.Position = start;
        while (stream.Position < end && TryReadHeader(stream, end, out var type, out var payloadStart, out var payloadEnd))
        {
            switch (type)
            {
                case "mdhd":
                    ReadMdhd(stream, payloadStart, payloadEnd, trak);
                    break;
                case "hdlr":
                    ReadHdlr(stream, payloadStart, payloadEnd, trak);
                    break;
            }

            stream.Position = payloadEnd;
        }
    }

    private static void ParseMvex(Stream stream, long start, long end, ParseState state)
    {
        stream.Position = start;
        while (stream.Position < end && TryReadHeader(stream, end, out var type, out var payloadStart, out var payloadEnd))
        {
            if (type == "trex")
            {
                ReadTrex(stream, payloadStart, payloadEnd, state);
            }

            stream.Position = payloadEnd;
        }
    }

    private static void ParseMoof(Stream stream, long start, long end, ParseState state)
    {
        stream.Position = start;
        while (stream.Position < end && TryReadHeader(stream, end, out var type, out var payloadStart, out var payloadEnd))
        {
            if (type == "traf")
            {
                ParseTraf(stream, payloadStart, payloadEnd, state);
            }

            stream.Position = payloadEnd;
        }
    }

    private static void ParseTraf(Stream stream, long start, long end, ParseState state)
    {
        stream.Position = start;
        uint trackId = 0;
        uint? tfhdDefaultDuration = null;
        while (stream.Position < end && TryReadHeader(stream, end, out var type, out var payloadStart, out var payloadEnd))
        {
            if (type == "tfhd")
            {
                ReadTfhd(stream, payloadStart, payloadEnd, out trackId, out tfhdDefaultDuration);
            }
            else if (type == "trun")
            {
                AccumulateTrun(stream, payloadStart, payloadEnd, state, trackId, tfhdDefaultDuration);
            }

            stream.Position = payloadEnd;
        }
    }

    private static void ReadMvhd(Stream stream, long start, long end, ParseState state)
    {
        var p = ReadPayload(stream, start, end);
        if (p == null || p.Length < 4)
        {
            return;
        }

        if (p[0] == 1)
        {
            if (p.Length < 32)
            {
                return;
            }

            state.MovieTimescale = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(20, 4));
            state.MovieDuration = BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(24, 8));
        }
        else
        {
            if (p.Length < 20)
            {
                return;
            }

            state.MovieTimescale = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12, 4));
            state.MovieDuration = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(16, 4));
        }
    }

    private static void ReadMdhd(Stream stream, long start, long end, TrakInfo trak)
    {
        var p = ReadPayload(stream, start, end);
        if (p == null || p.Length < 4)
        {
            return;
        }

        if (p[0] == 1)
        {
            if (p.Length < 32)
            {
                return;
            }

            trak.MediaTimescale = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(20, 4));
            trak.MediaDuration = BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(24, 8));
        }
        else
        {
            if (p.Length < 20)
            {
                return;
            }

            trak.MediaTimescale = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12, 4));
            trak.MediaDuration = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(16, 4));
        }
    }

    private static void ReadTkhd(Stream stream, long start, long end, TrakInfo trak)
    {
        var p = ReadPayload(stream, start, end);
        if (p == null || p.Length < 4)
        {
            return;
        }

        if (p[0] == 1)
        {
            if (p.Length < 24)
            {
                return;
            }

            trak.TrackId = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(20, 4));
        }
        else
        {
            if (p.Length < 16)
            {
                return;
            }

            trak.TrackId = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12, 4));
        }
    }

    private static void ReadHdlr(Stream stream, long start, long end, TrakInfo trak)
    {
        var p = ReadPayload(stream, start, end);
        if (p == null || p.Length < 12)
        {
            return;
        }

        trak.Handler = System.Text.Encoding.ASCII.GetString(p, 8, 4);
    }

    private static void ReadTrex(Stream stream, long start, long end, ParseState state)
    {
        var p = ReadPayload(stream, start, end);
        if (p == null || p.Length < 16)
        {
            return;
        }

        var trackId = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4, 4));
        var defaultDuration = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12, 4));
        state.TrexDefaultDuration[trackId] = defaultDuration;
    }

    private static void ReadTfhd(Stream stream, long start, long end, out uint trackId, out uint? defaultSampleDuration)
    {
        trackId = 0;
        defaultSampleDuration = null;

        var p = ReadPayload(stream, start, end);
        if (p == null || p.Length < 8)
        {
            return;
        }

        var flags = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(0, 4)) & 0xFFFFFFu;
        var pos = 4;
        trackId = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(pos, 4));
        pos += 4;

        if ((flags & 0x000001) != 0)
        {
            pos += 8; // base-data-offset
        }

        if ((flags & 0x000002) != 0)
        {
            pos += 4; // sample-description-index
        }

        if ((flags & 0x000008) != 0 && pos + 4 <= p.Length)
        {
            defaultSampleDuration = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(pos, 4));
        }
    }

    private static void AccumulateTrun(Stream stream, long start, long end, ParseState state, uint trackId, uint? tfhdDefaultDuration)
    {
        var p = ReadPayload(stream, start, end);
        if (p == null || p.Length < 8)
        {
            return;
        }

        var flags = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(0, 4)) & 0xFFFFFFu;
        var sampleCount = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4, 4));
        var pos = 8;

        if ((flags & 0x000001) != 0)
        {
            pos += 4; // data-offset
        }

        if ((flags & 0x000004) != 0)
        {
            pos += 4; // first-sample-flags
        }

        var durationPresent = (flags & 0x000100) != 0;
        var sizePresent = (flags & 0x000200) != 0;
        var sampleFlagsPresent = (flags & 0x000400) != 0;
        var compositionPresent = (flags & 0x000800) != 0;

        uint defaultDuration = tfhdDefaultDuration
            ?? (state.TrexDefaultDuration.TryGetValue(trackId, out var trex) ? trex : 0);

        ulong total = 0;
        for (uint i = 0; i < sampleCount; i++)
        {
            if (durationPresent)
            {
                if (pos + 4 > p.Length)
                {
                    break;
                }

                total += BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(pos, 4));
                pos += 4;
            }
            else
            {
                total += defaultDuration;
            }

            if (sizePresent)
            {
                pos += 4;
            }

            if (sampleFlagsPresent)
            {
                pos += 4;
            }

            if (compositionPresent)
            {
                pos += 4;
            }
        }

        state.FragmentUnits[trackId] = (state.FragmentUnits.TryGetValue(trackId, out var existing) ? existing : 0) + total;
    }

    private static double? Resolve(ParseState state)
    {
        if (state.Traks.Count == 0)
        {
            return null;
        }

        TrakInfo? video = null;
        foreach (var trak in state.Traks)
        {
            if (string.Equals(trak.Handler, "vide", StringComparison.Ordinal))
            {
                video = trak;
                break;
            }
        }

        video ??= state.Traks[0];

        // Progressive MP4: the duration is declared in the header.
        if (video.MediaDuration > 0 && video.MediaTimescale > 0)
        {
            return (double)video.MediaDuration / video.MediaTimescale;
        }

        if (state.MovieDuration > 0 && state.MovieTimescale > 0)
        {
            return (double)state.MovieDuration / state.MovieTimescale;
        }

        // Fragmented MP4: sum the per-sample durations from the movie fragments.
        if (video.MediaTimescale > 0
            && state.FragmentUnits.TryGetValue(video.TrackId, out var units)
            && units > 0)
        {
            return (double)units / video.MediaTimescale;
        }

        return null;
    }

    private static bool TryReadHeader(Stream stream, long parentEnd, out string type, out long payloadStart, out long payloadEnd)
    {
        type = string.Empty;
        payloadStart = 0;
        payloadEnd = 0;

        var boxStart = stream.Position;
        if (boxStart + 8 > parentEnd)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[8];
        if (!ReadExact(stream, header))
        {
            return false;
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        type = System.Text.Encoding.ASCII.GetString(header.Slice(4, 4));
        long headerSize = 8;

        if (size == 1)
        {
            Span<byte> ext = stackalloc byte[8];
            if (!ReadExact(stream, ext))
            {
                return false;
            }

            size = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = parentEnd - boxStart;
        }

        if (size < headerSize)
        {
            return false;
        }

        payloadStart = boxStart + headerSize;
        payloadEnd = boxStart + size;
        if (payloadEnd > parentEnd)
        {
            payloadEnd = parentEnd;
        }

        return true;
    }

    private static byte[]? ReadPayload(Stream stream, long start, long end)
    {
        var length = end - start;
        if (length <= 0 || length > MaxLeafBytes)
        {
            return null;
        }

        stream.Position = start;
        var buffer = new byte[length];
        return ReadExact(stream, buffer) ? buffer : null;
    }

    private static bool ReadExact(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer.Slice(total));
            if (read <= 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private sealed class ParseState
    {
        public uint MovieTimescale { get; set; }

        public ulong MovieDuration { get; set; }

        public List<TrakInfo> Traks { get; } = new();

        public Dictionary<uint, uint> TrexDefaultDuration { get; } = new();

        public Dictionary<uint, ulong> FragmentUnits { get; } = new();
    }

    private sealed class TrakInfo
    {
        public uint TrackId { get; set; }

        public string? Handler { get; set; }

        public uint MediaTimescale { get; set; }

        public ulong MediaDuration { get; set; }
    }
}

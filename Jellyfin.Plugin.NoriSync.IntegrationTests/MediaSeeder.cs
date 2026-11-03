using System;
using System.Buffers.Binary;
using System.IO;

namespace Jellyfin.Plugin.NoriSync.IntegrationTests;

// Minimal PCM WAV generator — deliberately avoids NAudio/ffmpeg so the fixture
// has no extra build-time deps. Writes a mono 16-bit sine wave at the requested
// sample rate and duration. The waveform analyzer decodes to 44.1 kHz via ffmpeg
// inside the container regardless of the source rate.
internal static class MediaSeeder
{
    public static void WriteSineWav(string path, int sampleRate, int seconds, double frequencyHz, double amplitude = 0.25)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var totalSamples = sampleRate * seconds;
        var dataBytes = totalSamples * 2;

        using var fs = File.Create(path);
        Span<byte> hdr = stackalloc byte[44];
        "RIFF"u8.CopyTo(hdr[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(hdr.Slice(8, 4));
        "fmt "u8.CopyTo(hdr.Slice(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(24, 4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(28, 4), (uint)(sampleRate * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(34, 2), 16);
        "data"u8.CopyTo(hdr.Slice(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(40, 4), (uint)dataBytes);
        fs.Write(hdr);

        Span<byte> sample = stackalloc byte[2];
        var twoPiFOverFs = 2.0 * Math.PI * frequencyHz / sampleRate;
        for (var i = 0; i < totalSamples; i++)
        {
            var value = (short)(Math.Sin(twoPiFOverFs * i) * amplitude * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(sample, value);
            fs.Write(sample);
        }
    }
}

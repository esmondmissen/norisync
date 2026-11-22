using System;
using System.Collections.Generic;
using Jellyfin.Plugin.NoriSync.Services;
using Xunit;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// The analysis task feeds both analysers the decode in pieces, so a long mix is never held
/// whole. Fed any way, each must give exactly the bytes its in-memory form gives: the waveform
/// and descriptor every client already has would otherwise all change (and re-send) at once.
/// </summary>
public class StreamedAnalysisTests
{
    private const int Sr = WaveformAnalyzer.SampleRate;

    public static IEnumerable<object[]> Lengths()
    {
        yield return new object[] { 0.05 };    // under one FFT window
        yield return new object[] { 4096.0 / Sr };
        yield return new object[] { 7.0 };     // too short for a descriptor
        yield return new object[] { 9.5 };
        yield return new object[] { 100.0 };   // head and tail overlap
        yield return new object[] { 200.0 };   // head and tail apart
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void TheWaveformIsTheSameFedInPieces(double seconds)
    {
        var samples = Track(seconds);
        var whole = WaveformAnalyzer.Analyze(samples, Sr);
        foreach (var pieces in PieceSizes(samples.Length))
        {
            var streamed = new WaveformAnalyzer.Accumulator(Sr);
            Feed(samples, pieces, streamed.Append);
            Assert.Equal(whole, streamed.Finish());
        }
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void TheDescriptorIsTheSameFedInPieces(double seconds)
    {
        var samples = Track(seconds);
        var whole = MixProfileAnalyzer.Analyze(samples, Sr);
        if (seconds >= 100)
        {
            Assert.NotNull(whole);   // precondition: the regions are exercised
        }

        foreach (var pieces in PieceSizes(samples.Length))
        {
            var streamed = new MixProfileAnalyzer.Accumulator(Sr);
            Feed(samples, pieces, streamed.Append);
            Assert.Equal(whole, streamed.Finish());
        }
    }

    [Fact]
    public void SilenceStaysNullFedInPieces()
    {
        var samples = new float[Sr * 120];
        var streamed = new MixProfileAnalyzer.Accumulator(Sr);
        Feed(samples, new[] { 16_384 }, streamed.Append);
        Assert.Null(streamed.Finish());
        Assert.Null(MixProfileAnalyzer.Analyze(samples, Sr));
    }

    private delegate void Append(ReadOnlySpan<float> samples);

    private static void Feed(float[] samples, int[] sizes, Append append)
    {
        var at = 0;
        for (var k = 0; at < samples.Length; k++)
        {
            var n = Math.Min(sizes[k % sizes.Length], samples.Length - at);
            append(samples.AsSpan(at, n));
            at += n;
        }
    }

    /// <summary>
    /// The decoder's own piece (64 KB of floats), one bigger than the track, nearly all of it
    /// then a short end (the last frames' windows reach back past what the first piece's
    /// frames needed), and irregular sizes that land every boundary somewhere new.
    /// </summary>
    private static IEnumerable<int[]> PieceSizes(int length)
    {
        yield return new[] { 16_384 };
        yield return new[] { Math.Max(1, length) };
        foreach (var end in new[] { 100, 1_000 })
        {
            if (length > end)
            {
                yield return new[] { length - end, end };
            }
        }

        var random = new Random(7);
        var irregular = new int[64];
        for (var i = 0; i < irregular.Length; i++)
        {
            irregular[i] = i % 8 == 0 ? random.Next(1, 300) : random.Next(1, 60_000);
        }

        yield return irregular;
    }

    /// <summary>A 123 BPM kick and hat pattern over noise, with a beatless intro.</summary>
    private static float[] Track(double seconds)
    {
        var samples = new float[(int)(seconds * Sr)];
        var random = new Random(1234);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.01 * ((random.NextDouble() * 2) - 1));
        }

        var period = 60.0 / 123;
        for (var t = 4.0; t < seconds - 0.3; t += period)
        {
            var start = (int)(t * Sr);
            var phase = 0.0;
            for (var i = 0; i < (int)(0.2 * Sr) && start + i < samples.Length; i++)
            {
                var s = i / (double)Sr;
                phase += 2 * Math.PI * (50 + (100 * Math.Exp(-s / 0.03))) / Sr;
                samples[start + i] += (float)(0.6 * Math.Exp(-s / 0.08) * Math.Sin(phase));
            }

            var hat = (int)((t + (period / 2)) * Sr);
            for (var i = 0; i < (int)(0.02 * Sr) && hat + i < samples.Length; i++)
            {
                samples[hat + i] += (float)(0.08 * ((random.NextDouble() * 2) - 1) * (1 - (i / (0.02 * Sr))));
            }
        }

        return samples;
    }
}

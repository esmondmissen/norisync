using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.NoriSync.Models;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Computes a track's beat "mix descriptor" (<c>mixprofile-v1</c>, 48 bytes, API_FORMAT §4.11)
/// from the mono PCM the waveform task already decodes: for the head (first 60 s) and tail
/// (last 90 s), an Ellis (2007) DP beat track whose sub-hop refined onsets are least-squares
/// fitted to a steady lattice, a best-effort bar phase, and where the outro recedes.
/// Pure and allocation-light: one pass over the samples, then work on 5.8 ms hops.
/// </summary>
public static class MixProfileAnalyzer
{
    /// <summary>
    /// The descriptor's format, as the change feed reports it (clients accept exactly this).
    /// Bump only on a change to the layout or to what a field means.
    /// </summary>
    public const string Version = "mixprofile-v1";

    /// <summary>
    /// What's persisted beside each descriptor: the format plus the analyser's revision.
    /// Bump the revision when the analysis changes but the format doesn't, so every track is
    /// analysed again while clients keep reading <see cref="Version"/>.
    /// </summary>
    public const string StoredVersion = Version + "/2";

    /// <summary>
    /// Exact length of every encoded descriptor.
    /// </summary>
    public const int BlobLength = 48;

    /// <summary>
    /// Encoded "no time" (<c>outroRecedesAt</c> absent).
    /// </summary>
    public const uint NoTime = uint.MaxValue;

    /// <summary>
    /// Encoded unknown downbeat offset.
    /// </summary>
    public const byte UnknownDownbeat = 255;

    /// <summary>
    /// Length of the head region, from the first decoded sample.
    /// </summary>
    public const double HeadSeconds = 60.0;

    /// <summary>
    /// Length of the tail region, ending at the last decoded sample.
    /// </summary>
    public const double TailSeconds = 90.0;

    /// <summary>
    /// A tracked beat counts as on the lattice within this distance of it.
    /// </summary>
    public const double LatticeToleranceSeconds = 0.015;

    private const byte FormatVersion = 0x01;
    private const byte FlagHeadValid = 0x01;
    private const byte FlagTailValid = 0x02;
    private const byte FlagHeadDownbeat = 0x04;
    private const byte FlagTailDownbeat = 0x08;
    private const int HeadOffset = 4;
    private const int TailOffset = 24;
    private const int OutroOffset = 44;
    private const double TicksPerSecond = 10_000.0;

    // 256-sample hops (5.8 ms at 44.1 kHz), 1024-sample (4-hop) frames.
    private const int Hop = 256;
    private const int HopsPerFrame = 4;

    private const double MinTrackSeconds = 8.0;
    private const double MinRegionSeconds = 8.0;

    // −80 dBFS mean square: below this the track is treated as silent.
    private const double SilentMeanSquare = 1e-8;

    // log(1 + g·ms): close to scale-invariant above −40 dBFS, linear (quiet) below.
    private const double LogGain = 1e4;

    private const double MinBpm = 60.0;
    private const double MaxBpm = 200.0;
    private const double PreferLowBpm = 80.0;
    private const double PreferHighBpm = 160.0;
    private const double TempoPriorBpm = 120.0;
    private const double TempoPriorOctaves = 1.0;

    // Comb tempo: the autocorrelation summed at this many multiples of a candidate period.
    private const int CombMultiples = 4;
    private const double CombLagStep = 0.05;

    // Grid fit: period within ±GridPeriodSpan of the tempo pick in GridPeriodStep steps, phase
    // in quarter frames.
    private const double GridPeriodSpan = 0.006;
    private const double GridPeriodStep = 0.0001;
    private const double GridPhaseStep = 0.25;

    // Grid confidence: a beat is clear when the envelope peaks within ±ClearPeakFrames of it
    // (12 ms) above the region's ClearPeakPercentile. Steadiness: the two halves' periods may
    // differ by FullySteadyDrift for full confidence, none at NotSteadyDrift.
    private const int ClearPeakFrames = 2;
    private const double ClearPeakPercentile = 0.75;
    private const double FullySteadyDrift = 0.001;
    private const double NotSteadyDrift = 0.005;

    // Sub-hop onset refinement: 2.9 ms windows every 16 samples, ±11.6 ms around the coarse beat.
    private const int RefineWindow = 128;
    private const int RefineHop = 16;
    private const int RefineSearch = 512;

    // A tracked beat is "strong" (real onset support, not DP filler through a beatless
    // stretch) when its broadband or low-band level over the 93 ms after it is at least
    // MinStrength nats (0.65 dB) above the 93 ms before it, and StrengthFraction of the
    // region's 95th-percentile beat. Windows that long average out the periodic ripple of a
    // steady chord, which one-hop flux reads as a perfect beat.
    private const int StrengthHops = 16;
    private const double MinStrength = 0.15;
    private const double StrengthFraction = 0.15;

    private const int MinSupportedBeats = 8;
    private const double FullConfidenceBeats = 16.0;
    private const double MinValidConfidence = 0.25;

    private const int MinDownbeatBars = 4;
    private const double DownbeatConfidentAt = 0.2;

    private const double LowBandLowHz = 40.0;
    private const double LowBandHighHz = 150.0;

    // The low band is filtered at 1/8 rate (5.5 kHz) after an 8-sample box average, whose
    // nulls sit on the images that would fold onto 40–150 Hz (≥ 30 dB down).
    private const int LowBandDecimation = 8;

    // Outro: ~46 ms blocks, 1.5 s centred smoothing, below half the 75th-percentile level.
    private const int OutroBlockHops = 8;
    private const double OutroSmoothingSeconds = 1.5;
    private const double OutroBodyPercentile = 0.75;
    private const double OutroRecedeRatio = 0.5;
    private const double MinOutroSeconds = 1.0;

    // −80 dB low-band RMS: a track with no low end has no body to recede from.
    private const double SilentBodyRms = 1e-4;

    /// <summary>
    /// The format a stored version describes, as the change feed reports it.
    /// </summary>
    /// <param name="storedVersion">A persisted version, with or without a revision.</param>
    /// <returns>The part before the revision; null stays null.</returns>
    public static string? WireVersion(string? storedVersion)
    {
        if (storedVersion == null)
        {
            return null;
        }

        var slash = storedVersion.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? storedVersion : storedVersion[..slash];
    }

    /// <summary>
    /// Analyses a track and returns the encoded 48-byte descriptor.
    /// </summary>
    /// <param name="samples">Mono PCM, the waveform task's decode.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <returns>The blob, or null for a track too short or too silent to give either region.</returns>
    public static byte[]? Analyze(float[] samples, int sampleRate)
    {
        var profile = AnalyzeProfile(samples, sampleRate);
        return profile == null ? null : Encode(profile);
    }

    /// <summary>
    /// Analyses a track. A region with no clear steady beat is null; an audible but beatless
    /// track still returns a profile (both regions null), so it isn't re-analysed every run.
    /// </summary>
    /// <param name="samples">Mono PCM, the waveform task's decode.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <returns>The profile, or null for a track too short or too silent to analyse.</returns>
    public static MixProfile? AnalyzeProfile(float[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0 || samples.Length < sampleRate * MinTrackSeconds)
        {
            return null;
        }

        var hops = samples.Length / Hop;
        var energies = new HopEnergies(sampleRate, hops);
        for (var h = 0; h < hops; h++)
        {
            energies.Add(new ReadOnlySpan<float>(samples, h * Hop, Hop));
        }

        return AnalyzeProfile(new TrackSamples(samples), sampleRate, energies);
    }

    private static MixProfile? AnalyzeProfile(TrackSamples samples, int sampleRate, HopEnergies energies)
    {
        if (energies.MeanSquare < SilentMeanSquare)
        {
            return null;
        }

        var broadband = energies.Broadband();
        var lowBand = energies.LowBand();
        var duration = samples.Length / (double)sampleRate;
        var head = AnalyzeRegion(samples, sampleRate, broadband, lowBand, 0, Math.Min(duration, HeadSeconds));
        var tail = AnalyzeRegion(samples, sampleRate, broadband, lowBand, Math.Max(0, duration - TailSeconds), duration);
        var outro = FindOutroRecedes(lowBand, sampleRate, duration);

        return new MixProfile
        {
            Head = head,
            Tail = tail,
            OutroRecedesAt = outro.HasValue ? ToTicks(outro.Value) : null
        };
    }

    /// <summary>
    /// Encodes a profile as the 48-byte little-endian <c>mixprofile-v1</c> blob.
    /// </summary>
    /// <param name="profile">The profile.</param>
    /// <returns>Exactly <see cref="BlobLength"/> bytes.</returns>
    public static byte[] Encode(MixProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var blob = new byte[BlobLength];
        blob[0] = 0x4D;
        blob[1] = 0x50;
        blob[2] = FormatVersion;

        byte flags = 0;
        if (profile.Head != null)
        {
            flags |= FlagHeadValid;
            if (profile.Head.DownbeatConfident)
            {
                flags |= FlagHeadDownbeat;
            }

            WriteRegion(blob.AsSpan(HeadOffset, 20), profile.Head);
        }

        if (profile.Tail != null)
        {
            flags |= FlagTailValid;
            if (profile.Tail.DownbeatConfident)
            {
                flags |= FlagTailDownbeat;
            }

            WriteRegion(blob.AsSpan(TailOffset, 20), profile.Tail);
        }

        blob[3] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(OutroOffset), profile.OutroRecedesAt ?? NoTime);
        return blob;
    }

    /// <summary>
    /// Decodes a <c>mixprofile-v1</c> blob, strictly: anything but the exact 48-byte layout with
    /// the <c>"MP"</c> magic and format byte 1 is null.
    /// </summary>
    /// <param name="blob">The encoded descriptor.</param>
    /// <returns>The profile, or null.</returns>
    public static MixProfile? Decode(ReadOnlySpan<byte> blob)
    {
        if (blob.Length != BlobLength || blob[0] != 0x4D || blob[1] != 0x50 || blob[2] != FormatVersion)
        {
            return null;
        }

        var flags = blob[3];
        var outro = BinaryPrimitives.ReadUInt32LittleEndian(blob[OutroOffset..]);
        return new MixProfile
        {
            Head = (flags & FlagHeadValid) != 0 ? ReadRegion(blob.Slice(HeadOffset, 20), (flags & FlagHeadDownbeat) != 0) : null,
            Tail = (flags & FlagTailValid) != 0 ? ReadRegion(blob.Slice(TailOffset, 20), (flags & FlagTailDownbeat) != 0) : null,
            OutroRecedesAt = outro == NoTime ? null : outro
        };
    }

    private static void WriteRegion(Span<byte> span, MixProfileRegion region)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span, region.PeriodMicros);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], region.ReferenceBeat);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], region.FirstBeat);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], region.LastBeat);
        span[16] = region.Confidence;
        span[17] = region.DownbeatOffset;
        span[18] = region.DownbeatConfidence;
        span[19] = 0;
    }

    private static MixProfileRegion ReadRegion(ReadOnlySpan<byte> span, bool downbeatConfident) => new()
    {
        PeriodMicros = BinaryPrimitives.ReadUInt32LittleEndian(span),
        ReferenceBeat = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
        FirstBeat = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
        LastBeat = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]),
        Confidence = span[16],
        DownbeatOffset = span[17],
        DownbeatConfidence = span[18],
        DownbeatConfident = downbeatConfident
    };

    private static uint ToTicks(double seconds)
    {
        var ticks = Math.Round(Math.Max(0, seconds) * TicksPerSecond);
        return ticks >= NoTime ? NoTime - 1 : (uint)ticks;
    }

    private static byte ToByte(double fraction) => (byte)Math.Round(Math.Clamp(fraction, 0, 1) * 255);

    private static float SumOfSquares(ReadOnlySpan<float> values)
    {
        var vectors = MemoryMarshal.Cast<float, Vector<float>>(values);
        var acc = Vector<float>.Zero;
        foreach (var v in vectors)
        {
            acc += v * v;
        }

        var sum = Vector.Sum(acc);
        for (var i = vectors.Length * Vector<float>.Count; i < values.Length; i++)
        {
            sum += values[i] * values[i];
        }

        return sum;
    }

    private static MixProfileRegion? AnalyzeRegion(
        TrackSamples samples,
        int sampleRate,
        float[] broadband,
        float[] lowBand,
        double start,
        double end)
    {
        if (end - start < MinRegionSeconds)
        {
            return null;
        }

        var firstHop = (int)Math.Floor(start * sampleRate / Hop);
        var endHop = Math.Min(broadband.Length, (int)Math.Floor(end * sampleRate / Hop));
        var frameCount = endHop - firstHop - HopsPerFrame + 1;
        if (frameCount < 64)
        {
            return null;
        }

        var envelope = OnsetEnvelope(broadband, lowBand, firstHop, frameCount);
        if (envelope == null)
        {
            return null;
        }

        var frameRate = (double)sampleRate / Hop;
        var period = CombPeriodFrames(envelope, frameRate);
        if (period == null)
        {
            return null;
        }

        var grid = FitGrid(envelope, period.Value);
        var frames = GridFrames(grid.Period, grid.Phase, envelope.Length);
        if (frames.Length < MinSupportedBeats)
        {
            return null;
        }

        var scratch = new double[(2 * RefineSearch) + RefineWindow + 1];
        var beats = new TrackedBeat[frames.Length];
        for (var i = 0; i < frames.Length; i++)
        {
            var f = frames[i];

            // Frame f's flux is the onset entering its LAST hop (frame f−1 didn't hold it).
            var coarse = ((long)(firstHop + f + HopsPerFrame - 1) * Hop) + (Hop / 2);
            var time = RefineOnset(samples, sampleRate, coarse, scratch);
            var onsetHop = (int)(time * sampleRate / Hop);
            var strength = Math.Max(LevelRise(broadband, onsetHop), LevelRise(lowBand, onsetHop));
            beats[i] = new TrackedBeat(time, strength);
        }

        return FitRegion(beats, start, end, sampleRate, lowBand, GridConfidence(envelope, grid.Period, grid.Phase));
    }

    // Log level of the StrengthHops after onsetHop over the StrengthHops before it, in nats.
    private static double LevelRise(float[] hopEnergy, int onsetHop)
    {
        double before = 0, after = 0;
        for (var j = 1; j <= StrengthHops; j++)
        {
            var b = onsetHop - j;
            var a = onsetHop + j - 1;
            before += b >= 0 && b < hopEnergy.Length ? hopEnergy[b] : 0;
            after += a >= 0 && a < hopEnergy.Length ? hopEnergy[a] : 0;
        }

        var scale = LogGain / (StrengthHops * Hop);
        return Math.Log(1 + (scale * after)) - Math.Log(1 + (scale * before));
    }

    /// <summary>
    /// Half-wave rectified log-energy flux of the broadband and low-band frames, each in std
    /// units, summed: kicks lead, but a beat carried by snare or hats still registers.
    /// </summary>
    private static double[]? OnsetEnvelope(float[] broadband, float[] lowBand, int firstHop, int frameCount)
    {
        var bandFlux = LogFlux(broadband, firstHop, frameCount);
        var lowFlux = LogFlux(lowBand, firstHop, frameCount);
        var bandStd = StandardDeviation(bandFlux);
        var lowStd = StandardDeviation(lowFlux);
        if (bandStd <= 1e-12 && lowStd <= 1e-12)
        {
            return null;
        }

        var envelope = new double[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            var v = 0.0;
            if (bandStd > 1e-12)
            {
                v += bandFlux[f] / bandStd;
            }

            if (lowStd > 1e-12)
            {
                v += lowFlux[f] / lowStd;
            }

            envelope[f] = v;
        }

        var std = StandardDeviation(envelope);
        for (var f = 0; f < frameCount; f++)
        {
            envelope[f] /= std;
        }

        return envelope;
    }

    private static double[] LogFlux(float[] hopEnergy, int firstHop, int frameCount)
    {
        var flux = new double[frameCount];
        var previous = 0.0;
        for (var f = 0; f < frameCount; f++)
        {
            double sum = 0;
            for (var j = 0; j < HopsPerFrame; j++)
            {
                sum += hopEnergy[firstHop + f + j];
            }

            var level = Math.Log(1.0 + (LogGain * sum / (Hop * HopsPerFrame)));
            if (f > 0 && level > previous)
            {
                flux[f] = level - previous;
            }

            previous = level;
        }

        return flux;
    }

    /// <summary>
    /// Tempo by autocorrelation of the envelope, weighted by a log-Gaussian prior around
    /// 120 BPM (Ellis), with a parabolic peak refinement. In envelope frames.
    /// </summary>
    private static double? EstimatePeriodFrames(double[] envelope, double frameRate)
    {
        var n = envelope.Length;
        var minLag = (int)Math.Floor(60.0 * frameRate / MaxBpm);
        var maxLag = Math.Min((int)Math.Ceiling(60.0 * frameRate / MinBpm), (n / 2) - 1);
        if (minLag < 2 || maxLag <= minLag)
        {
            return null;
        }

        double mean = 0;
        for (var i = 0; i < n; i++)
        {
            mean += envelope[i];
        }

        mean /= n;
        var centred = new double[n];
        for (var i = 0; i < n; i++)
        {
            centred[i] = envelope[i] - mean;
        }

        var acf = new double[maxLag + 2];
        for (var lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            double acc = 0;
            var limit = n - lag;
            for (var i = 0; i < limit; i++)
            {
                acc += centred[i] * centred[i + lag];
            }

            // Per overlapping frame, so long lags aren't penalised for their shorter overlap.
            acf[lag] = acc / limit;
        }

        var best = -1;
        var bestScore = 0.0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            if (acf[lag] <= 0)
            {
                continue;
            }

            var octaves = Math.Log2(60.0 * frameRate / lag / TempoPriorBpm) / TempoPriorOctaves;
            var score = acf[lag] * Math.Exp(-0.5 * octaves * octaves);
            if (score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }

        if (best < 0)
        {
            return null;
        }

        var a = acf[best - 1];
        var b = acf[best];
        var c = acf[best + 1];
        var denominator = a - (2 * b) + c;
        var delta = denominator < 0 ? Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5) : 0.0;
        return best + delta;
    }

    /// <summary>
    /// Tempo by comb: the autocorrelation at the first <see cref="CombMultiples"/> multiples of
    /// each candidate period, together, under the same octave prior as
    /// <see cref="EstimatePeriodFrames"/>. A true beat period lines up at every multiple; a 3:2
    /// or 2:3 impostor (arpeggios, swing) misses some — the single-lag pick took Daft Punk's
    /// "Aerodynamic" at 81.9 BPM, two-thirds of its 122.9. In envelope frames.
    /// </summary>
    private static double? CombPeriodFrames(double[] envelope, double frameRate)
    {
        var n = envelope.Length;
        var minLag = 60.0 * frameRate / MaxBpm;
        var maxLag = 60.0 * frameRate / MinBpm;
        var maxAcfLag = (int)Math.Ceiling(maxLag * CombMultiples) + 2;
        if (maxAcfLag >= n / 2)
        {
            // Too short a region for the longer multiples.
            return EstimatePeriodFrames(envelope, frameRate);
        }

        double mean = 0;
        for (var i = 0; i < n; i++)
        {
            mean += envelope[i];
        }

        mean /= n;
        var centred = new double[n];
        for (var i = 0; i < n; i++)
        {
            centred[i] = envelope[i] - mean;
        }

        var acf = new double[maxAcfLag + 1];
        for (var lag = 0; lag <= maxAcfLag; lag++)
        {
            double acc = 0;
            var limit = n - lag;
            for (var i = 0; i < limit; i++)
            {
                acc += centred[i] * centred[i + lag];
            }

            // Per overlapping frame, so long lags aren't penalised for their shorter overlap.
            acf[lag] = acc / limit;
        }

        var best = -1.0;
        var bestScore = 0.0;
        for (var lag = minLag; lag <= maxLag; lag += CombLagStep)
        {
            double comb = 0;
            for (var k = 1; k <= CombMultiples; k++)
            {
                var at = k * lag;
                var i = (int)at;
                var t = at - i;
                comb += (acf[i] * (1 - t)) + (acf[i + 1] * t);
            }

            if (comb <= 0)
            {
                continue;
            }

            var octaves = Math.Log2(60.0 * frameRate / lag / TempoPriorBpm) / TempoPriorOctaves;
            var score = comb * Math.Exp(-0.5 * octaves * octaves);
            if (score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }

        return best < 0 ? null : best;
    }

    /// <summary>
    /// The steady lattice covering the most onset envelope, searched directly: period within
    /// ±<see cref="GridPeriodSpan"/> of the tempo pick, phase over one period. The beat mix
    /// needs a steady region and nothing else, so no beat-by-beat tracking: a tracker followed
    /// arpeggios and breakdowns off the beat and lost sections a steady grid covers.
    /// </summary>
    private static (double Period, double Phase) FitGrid(double[] envelope, double period)
    {
        double bestPeriod = period, bestPhase = 0, bestScore = double.NegativeInfinity;
        for (var p = period * (1 - GridPeriodSpan); p <= period * (1 + GridPeriodSpan); p += period * GridPeriodStep)
        {
            for (var phase = 0.0; phase < p; phase += GridPhaseStep)
            {
                var score = GridScore(envelope, p, phase, 0, envelope.Length);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPeriod = p;
                    bestPhase = phase;
                }
            }
        }

        return (bestPeriod, bestPhase);
    }

    /// <summary>The mean envelope at the lattice's beats inside frames [from, to).</summary>
    private static double GridScore(double[] envelope, double period, double phase, int from, int to)
    {
        var first = phase + (Math.Ceiling((from - phase) / period) * period);
        double sum = 0;
        var count = 0;
        for (var f = first; f < to - 1; f += period)
        {
            var i = (int)f;
            if (i < 0)
            {
                continue;
            }

            var t = f - i;
            sum += (envelope[i] * (1 - t)) + (envelope[i + 1] * t);
            count++;
        }

        return count == 0 ? double.NegativeInfinity : sum / count;
    }

    private static int[] GridFrames(double period, double phase, int frameCount)
    {
        var frames = new List<int>();
        for (var f = phase; f < frameCount - 1; f += period)
        {
            frames.Add((int)Math.Round(f));
        }

        return frames.ToArray();
    }

    /// <summary>
    /// How far the grid can be trusted for beat-matching: the share of its beats with a clear
    /// onset (an envelope peak within ±<see cref="ClearPeakFrames"/>, above the region's
    /// <see cref="ClearPeakPercentile"/>), times how steady it is — the same fit on each half
    /// of the region must find the same period. Beats with no attack (a breakdown, a sparse
    /// kick pattern) only cost clarity; a drifting tempo costs everything.
    /// </summary>
    private static double GridConfidence(double[] envelope, double period, double phase)
    {
        var n = envelope.Length;
        var sorted = (double[])envelope.Clone();
        Array.Sort(sorted);
        var threshold = sorted[(int)(n * ClearPeakPercentile)];
        int clear = 0, total = 0;
        for (var f = phase; f < n - 1; f += period)
        {
            total++;
            var centre = (int)Math.Round(f);
            for (var d = -ClearPeakFrames; d <= ClearPeakFrames; d++)
            {
                var j = centre + d;
                if (j > 0 && j + 1 < n && envelope[j] >= threshold
                    && envelope[j] >= envelope[j - 1] && envelope[j] >= envelope[j + 1])
                {
                    clear++;
                    break;
                }
            }
        }

        if (total == 0)
        {
            return 0;
        }

        var drift = Math.Abs(HalfPeriod(envelope, period, phase, 0, n / 2) - HalfPeriod(envelope, period, phase, n / 2, n)) / period;
        var steadiness = Math.Clamp((NotSteadyDrift - drift) / (NotSteadyDrift - FullySteadyDrift), 0, 1);
        return (double)clear / total * steadiness;
    }

    /// <summary>The grid's period refitted on frames [from, to) alone (±1 %, phase ±2 frames).</summary>
    private static double HalfPeriod(double[] envelope, double period, double phase, int from, int to)
    {
        double bestPeriod = period, bestScore = double.NegativeInfinity;
        for (var p = period * 0.99; p <= period * 1.01; p += period * GridPeriodStep)
        {
            for (var shift = -2.0; shift <= 2.0; shift += 0.5)
            {
                // Anchored at the grid beat nearest the half's middle, so the period, not the
                // phase, decides the fit.
                var middle = (from + to) / 2.0;
                var anchor = phase + (Math.Round((middle - phase) / period) * period) + shift;
                var score = GridScore(envelope, p, anchor - (Math.Floor((anchor - from) / p) * p), from, to);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPeriod = p;
                }
            }
        }

        return bestPeriod;
    }

    /// <summary>
    /// The onset near a coarse beat to ~0.4 ms: the 2.9 ms window with the largest log-energy
    /// rise, timed at the 16 samples it took in. A log ratio peaks at the first samples of an
    /// attack, so this times the attack's start rather than its peak.
    /// </summary>
    private static double RefineOnset(TrackSamples samples, int sampleRate, long coarse, double[] prefix)
    {
        var lo = Math.Max(0, coarse - RefineSearch - RefineWindow);
        var hi = Math.Min(samples.Length, coarse + RefineSearch);
        var length = (int)(hi - lo);
        if (length < RefineWindow + RefineHop)
        {
            return coarse / (double)sampleRate;
        }

        prefix[0] = 0;
        for (var i = 0; i < length; i++)
        {
            double x = samples[lo + i];
            prefix[i + 1] = prefix[i] + (x * x);
        }

        var bestFlux = 0.05;
        var bestEnd = -1;
        var previous = double.NaN;
        for (var e = RefineWindow; e <= length; e += RefineHop)
        {
            var meanSquare = Math.Max(0, prefix[e] - prefix[e - RefineWindow]) / RefineWindow;
            var level = Math.Log10(meanSquare + 1e-10);
            if (!double.IsNaN(previous) && level - previous > bestFlux)
            {
                bestFlux = level - previous;
                bestEnd = e;
            }

            previous = level;
        }

        if (bestEnd < 0)
        {
            return coarse / (double)sampleRate;
        }

        return (lo + bestEnd - (RefineHop / 2.0)) / sampleRate;
    }

    private static MixProfileRegion? FitRegion(TrackedBeat[] beats, double start, double end, int sampleRate, float[] lowBand, double confidence)
    {
        var strengths = new double[beats.Length];
        for (var i = 0; i < beats.Length; i++)
        {
            strengths[i] = beats[i].Strength;
        }

        var strongCut = Math.Max(MinStrength, StrengthFraction * Percentile(strengths, 0.95));
        var strong = new List<TrackedBeat>(beats.Length);
        foreach (var beat in beats)
        {
            if (beat.Strength >= strongCut)
            {
                strong.Add(beat);
            }
        }

        if (strong.Count < MinSupportedBeats)
        {
            return null;
        }

        var intervals = new double[beats.Length - 1];
        for (var i = 1; i < beats.Length; i++)
        {
            intervals[i - 1] = beats[i].Time - beats[i - 1].Time;
        }

        var lattice = FitLattice(strong, strong[strong.Count / 2].Time, Percentile(intervals, 0.5));
        if (lattice == null)
        {
            return null;
        }

        var (reference, period) = lattice.Value;
        var lowerK = (int)Math.Ceiling(((start - reference) / period) - 1e-9);
        var upperK = (int)Math.Floor(((end - reference) / period) + 1e-9);
        if (upperK - lowerK < MinSupportedBeats)
        {
            return null;
        }

        var supported = new bool[upperK - lowerK + 1];
        foreach (var beat in strong)
        {
            var k = (int)Math.Round((beat.Time - reference) / period);
            if (k >= lowerK && k <= upperK && Math.Abs(beat.Time - (reference + (k * period))) <= LatticeToleranceSeconds)
            {
                supported[k - lowerK] = true;
            }
        }

        var first = FindRunStart(supported);
        var last = FindRunEnd(supported);
        if (first < 0 || last < first)
        {
            return null;
        }

        var supportedCount = 0;
        var supportedIndexSum = 0L;
        for (var i = first; i <= last; i++)
        {
            if (supported[i])
            {
                supportedCount++;
                supportedIndexSum += i;
            }
        }

        // The grid's confidence (`GridConfidence`), scaled down while fewer than 16 beats support it.
        confidence *= Math.Min(1.0, supportedCount / FullConfidenceBeats);
        if (supportedCount < MinSupportedBeats || confidence < MinValidConfidence)
        {
            return null;
        }

        // Re-index around a reference beat at the middle of the supported beats.
        var referenceIndex = (int)Math.Round((double)supportedIndexSum / supportedCount);
        while (!supported[referenceIndex])
        {
            referenceIndex++;
        }

        reference += (referenceIndex + lowerK) * period;
        var kFirst = first - referenceIndex;
        var kLast = last - referenceIndex;

        FoldTempo(ref reference, ref period, ref kFirst, ref kLast, sampleRate, lowBand);
        var (downbeatOffset, downbeatConfidence, bars) = FindDownbeat(reference, period, kFirst, kLast, sampleRate, lowBand);
        var downbeatKnown = bars >= MinDownbeatBars && ToByte(downbeatConfidence) > 0;

        return new MixProfileRegion
        {
            PeriodMicros = (uint)Math.Round(period * 1_000_000),
            ReferenceBeat = ToTicks(reference),
            FirstBeat = ToTicks(reference + (kFirst * period)),
            LastBeat = ToTicks(reference + (kLast * period)),
            Confidence = ToByte(confidence),
            DownbeatOffset = downbeatKnown ? (byte)downbeatOffset : UnknownDownbeat,
            DownbeatConfidence = downbeatKnown ? ToByte(downbeatConfidence) : (byte)0,
            DownbeatConfident = downbeatKnown && downbeatConfidence >= DownbeatConfidentAt
        };
    }

    /// <summary>
    /// Iterative least squares of beat time on beat index, tightening the inlier window from a
    /// quarter period down to the lattice tolerance and re-indexing each pass. Returns the
    /// lattice anchored at the inliers' centroid beat (the least-correlated reference).
    /// </summary>
    private static (double Reference, double Period)? FitLattice(List<TrackedBeat> beats, double anchor, double initialPeriod)
    {
        if (initialPeriod <= 0)
        {
            return null;
        }

        var reference = anchor;
        var period = initialPeriod;
        double[] windows = { 0.25 * initialPeriod, 0.06, 0.03, LatticeToleranceSeconds, LatticeToleranceSeconds };
        foreach (var window in windows)
        {
            double n = 0, sk = 0, st = 0, skk = 0, skt = 0;
            foreach (var beat in beats)
            {
                var k = Math.Round((beat.Time - reference) / period);
                var t = beat.Time - reference;
                if (Math.Abs(t - (k * period)) > window)
                {
                    continue;
                }

                n++;
                sk += k;
                st += t;
                skk += k * k;
                skt += k * t;
            }

            var denominator = (n * skk) - (sk * sk);
            if (n < 4 || denominator <= 0)
            {
                return null;
            }

            var slope = ((n * skt) - (sk * st)) / denominator;
            var intercept = (st - (slope * sk)) / n;
            if (slope < 60.0 / 300.0 || slope > 60.0 / 40.0)
            {
                return null;
            }

            reference += intercept + (slope * Math.Round(sk / n));
            period = slope;
        }

        return (reference, period);
    }

    // First supported lattice beat that starts a run of at least 3 supported in 4: a stray
    // onset in a beatless intro doesn't start the region's beats.
    private static int FindRunStart(bool[] supported)
    {
        for (var i = 0; i < supported.Length; i++)
        {
            if (supported[i] && CountSupported(supported, i, i + 3) >= Math.Min(3, supported.Length - i))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindRunEnd(bool[] supported)
    {
        for (var i = supported.Length - 1; i >= 0; i--)
        {
            if (supported[i] && CountSupported(supported, i - 3, i) >= Math.Min(3, i + 1))
            {
                return i;
            }
        }

        return -1;
    }

    private static int CountSupported(bool[] supported, int from, int to)
    {
        var count = 0;
        for (var i = Math.Max(0, from); i <= Math.Min(supported.Length - 1, to); i++)
        {
            if (supported[i])
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Folds the tracked tempo into 80–160 BPM. Doubling keeps every beat (the new ones fall
    /// between); halving keeps the parity with more low-band weight, the likelier strong beats.
    /// Indices stay relative to the reference beat, which stays inside [kFirst, kLast].
    /// </summary>
    private static void FoldTempo(ref double reference, ref double period, ref int kFirst, ref int kLast, int sampleRate, float[] lowBand)
    {
        while (60.0 / period < PreferLowBpm)
        {
            period /= 2;
            kFirst *= 2;
            kLast *= 2;
        }

        while (60.0 / period > PreferHighBpm && kLast - kFirst >= 2)
        {
            double even = 0, odd = 0;
            for (var k = kFirst; k <= kLast; k++)
            {
                var accent = LowBandAccent(lowBand, sampleRate, reference + (k * period), period);
                if ((k & 1) == 0)
                {
                    even += accent;
                }
                else
                {
                    odd += accent;
                }
            }

            if (odd > even)
            {
                // Move the reference to an odd neighbour inside the span.
                var shift = kLast >= 1 ? 1 : -1;
                reference += shift * period;
                kFirst -= shift;
                kLast -= shift;
            }

            // Even indices survive: round the span inwards onto them, then halve.
            kFirst = (int)Math.Ceiling(kFirst / 2.0);
            kLast = (int)Math.Floor(kLast / 2.0);
            period *= 2;
        }
    }

    /// <summary>
    /// Bar phase from where low-band energy falls within each whole bar of 4 lattice beats,
    /// each bar normalised so loud and quiet sections vote alike. Confidence is how far the
    /// winning phase stands above the runner-up (0 for an even four-on-the-floor).
    /// </summary>
    private static (int Offset, double Confidence, int Bars) FindDownbeat(double reference, double period, int kFirst, int kLast, int sampleRate, float[] lowBand)
    {
        var share = new double[4];
        var accents = new double[4];
        var bars = 0;
        for (var bar = FloorDiv(kFirst, 4); (bar * 4) + 3 <= kLast; bar++)
        {
            if (bar * 4 < kFirst)
            {
                continue;
            }

            double total = 0;
            for (var m = 0; m < 4; m++)
            {
                accents[m] = LowBandAccent(lowBand, sampleRate, reference + (((bar * 4) + m) * period), period);
                total += accents[m];
            }

            if (total <= 1e-12)
            {
                continue;
            }

            for (var m = 0; m < 4; m++)
            {
                share[m] += accents[m] / total;
            }

            bars++;
        }

        if (bars == 0)
        {
            return (0, 0, 0);
        }

        var best = 0;
        for (var m = 1; m < 4; m++)
        {
            if (share[m] > share[best])
            {
                best = m;
            }
        }

        var second = 0.0;
        for (var m = 0; m < 4; m++)
        {
            if (m != best && share[m] > second)
            {
                second = share[m];
            }
        }

        return (best, share[best] <= 0 ? 0 : (share[best] - second) / share[best], bars);
    }

    // Low-band energy from just before a beat to a little after it (the kick / bass note).
    private static double LowBandAccent(float[] lowBand, int sampleRate, double time, double period)
    {
        var from = (int)Math.Floor((time - 0.01) * sampleRate / Hop);
        var to = (int)Math.Ceiling((time + Math.Min(0.5 * period, 0.15)) * sampleRate / Hop);
        double sum = 0;
        for (var h = Math.Max(0, from); h < Math.Min(lowBand.Length, to); h++)
        {
            sum += lowBand[h];
        }

        return sum;
    }

    /// <summary>
    /// The time after which the 1.5 s-smoothed low-band level stays below half the track's body
    /// (its 75th percentile) to the end, sharpened to the last unsmoothed block above that
    /// line. Null when the track has no low end, or the line is crossed within the last second.
    /// </summary>
    private static double? FindOutroRecedes(float[] lowBand, int sampleRate, double duration)
    {
        var blockCount = (lowBand.Length + OutroBlockHops - 1) / OutroBlockHops;
        if (blockCount == 0)
        {
            return null;
        }

        var blockSeconds = OutroBlockHops * Hop / (double)sampleRate;
        var blockMeanSquare = new double[blockCount];
        var prefix = new double[blockCount + 1];
        for (var b = 0; b < blockCount; b++)
        {
            var from = b * OutroBlockHops;
            var to = Math.Min(lowBand.Length, from + OutroBlockHops);
            double sum = 0;
            for (var h = from; h < to; h++)
            {
                sum += lowBand[h];
            }

            blockMeanSquare[b] = sum / ((to - from) * Hop);
            prefix[b + 1] = prefix[b] + blockMeanSquare[b];
        }

        var radius = (int)Math.Round(OutroSmoothingSeconds / 2 / blockSeconds);
        var smoothed = new double[blockCount];
        for (var b = 0; b < blockCount; b++)
        {
            var from = Math.Max(0, b - radius);
            var to = Math.Min(blockCount, b + radius + 1);
            smoothed[b] = Math.Sqrt((prefix[to] - prefix[from]) / (to - from));
        }

        var body = Percentile(smoothed, OutroBodyPercentile);
        if (body < SilentBodyRms)
        {
            return null;
        }

        var threshold = OutroRecedeRatio * body;
        var last = blockCount - 1;
        while (last >= 0 && smoothed[last] < threshold)
        {
            last--;
        }

        if (last < 0)
        {
            return null;
        }

        var sharp = last;
        for (var b = Math.Min(blockCount - 1, last + radius); b >= Math.Max(0, last - radius); b--)
        {
            if (Math.Sqrt(blockMeanSquare[b]) >= threshold)
            {
                sharp = b;
                break;
            }
        }

        var recedesAt = (sharp + 1) * blockSeconds;
        return duration - recedesAt < MinOutroSeconds ? null : recedesAt;
    }

    private static double Percentile(double[] values, double fraction)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[(int)Math.Round(fraction * (sorted.Length - 1))];
    }

    private static double StandardDeviation(double[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        double mean = 0;
        foreach (var v in values)
        {
            mean += v;
        }

        mean /= values.Length;
        double variance = 0;
        foreach (var v in values)
        {
            variance += (v - mean) * (v - mean);
        }

        return Math.Sqrt(variance / values.Length);
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

    private readonly record struct TrackedBeat(double Time, double Strength);

    /// <summary>
    /// RBJ cookbook second-order Butterworth section, normalised by a0.
    /// </summary>
    private readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
    {
        public static Biquad LowPass(double cutoff, double sampleRate)
        {
            var (cos, alpha) = Prewarp(cutoff, sampleRate);
            var a0 = 1 + alpha;
            return new Biquad((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        public static Biquad HighPass(double cutoff, double sampleRate)
        {
            var (cos, alpha) = Prewarp(cutoff, sampleRate);
            var a0 = 1 + alpha;
            return new Biquad((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
        }

        private static (double Cos, double Alpha) Prewarp(double cutoff, double sampleRate)
        {
            var w0 = 2 * Math.PI * cutoff / sampleRate;
            return (Math.Cos(w0), Math.Sin(w0) / (2 * Math.Sqrt(0.5)));
        }
    }

    /// <summary>
    /// The descriptor of a track fed in pieces, as its decode arrives: per-hop energies of the
    /// whole track, but raw samples only of the head and tail regions (which beat refinement
    /// reads), so memory doesn't grow with the track — a multi-hour mix held whole ran a server
    /// out of memory. Gives exactly what <see cref="Analyze"/> gives on the same samples.
    /// </summary>
    internal sealed class Accumulator
    {
        // Past each region: the reach of beat refinement round an onset near its edge.
        private const int RegionMargin = 4096;

        private readonly int _sampleRate;
        private readonly HopEnergies _energies;
        private readonly float[] _hop = new float[Hop];
        private readonly float[] _head;
        private readonly float[] _tail;
        private int _hopFill;
        private int _headCount;
        private long _received;

        /// <summary>
        /// Initializes a new instance of the <see cref="Accumulator"/> class.
        /// </summary>
        /// <param name="sampleRate">Sample rate of the samples to come, in Hz.</param>
        public Accumulator(int sampleRate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
            _sampleRate = sampleRate;
            _energies = new HopEnergies(sampleRate, 0);
            _head = new float[(int)(HeadSeconds * sampleRate) + RegionMargin];
            _tail = new float[(int)(TailSeconds * sampleRate) + RegionMargin];
        }

        /// <summary>
        /// Feeds the next samples of the track.
        /// </summary>
        /// <param name="samples">Mono PCM, in order.</param>
        public void Append(ReadOnlySpan<float> samples)
        {
            if (_headCount < _head.Length)
            {
                var n = Math.Min(samples.Length, _head.Length - _headCount);
                samples[..n].CopyTo(_head.AsSpan(_headCount));
                _headCount += n;
            }

            // The tail is a ring: sample k lives at k mod its length.
            var keep = samples.Length > _tail.Length ? samples[^_tail.Length..] : samples;
            var at = (int)((_received + samples.Length - keep.Length) % _tail.Length);
            var first = Math.Min(keep.Length, _tail.Length - at);
            keep[..first].CopyTo(_tail.AsSpan(at));
            keep[first..].CopyTo(_tail);

            var rest = samples;
            if (_hopFill > 0)
            {
                var n = Math.Min(rest.Length, Hop - _hopFill);
                rest[..n].CopyTo(_hop.AsSpan(_hopFill));
                _hopFill += n;
                rest = rest[n..];
                if (_hopFill == Hop)
                {
                    _energies.Add(_hop);
                    _hopFill = 0;
                }
            }

            while (rest.Length >= Hop)
            {
                _energies.Add(rest[..Hop]);
                rest = rest[Hop..];
            }

            rest.CopyTo(_hop.AsSpan(_hopFill));
            _hopFill += rest.Length;
            _received += samples.Length;
        }

        /// <summary>
        /// The encoded descriptor of everything fed.
        /// </summary>
        /// <returns>The blob, or null as <see cref="Analyze"/> returns it.</returns>
        public byte[]? Finish()
        {
            var profile = FinishProfile();
            return profile == null ? null : Encode(profile);
        }

        /// <summary>
        /// The profile of everything fed.
        /// </summary>
        /// <returns>The profile, or null as <see cref="AnalyzeProfile(float[], int)"/> returns it.</returns>
        public MixProfile? FinishProfile()
        {
            if (_received < _sampleRate * MinTrackSeconds)
            {
                return null;
            }

            var tailCount = (int)Math.Min(_received, _tail.Length);
            var tailStart = _received - tailCount;
            var tail = new float[tailCount];
            var at = (int)(tailStart % _tail.Length);
            var first = Math.Min(tailCount, _tail.Length - at);
            _tail.AsSpan(at, first).CopyTo(tail);
            _tail.AsSpan(0, tailCount - first).CopyTo(tail.AsSpan(first));
            return AnalyzeProfile(new TrackSamples(_head, _headCount, tail, tailStart, _received), _sampleRate, _energies);
        }
    }

    /// <summary>
    /// The decoded track as the analysis reads it: every sample, or a streamed decode's head
    /// and tail (every index the regions read lies in one of them).
    /// </summary>
    private sealed class TrackSamples
    {
        private readonly float[] _head;
        private readonly int _headCount;
        private readonly float[] _tail;
        private readonly long _tailStart;

        public TrackSamples(float[] all)
            : this(all, all.Length, Array.Empty<float>(), all.Length, all.Length)
        {
        }

        public TrackSamples(float[] head, int headCount, float[] tail, long tailStart, long length)
        {
            _head = head;
            _headCount = headCount;
            _tail = tail;
            _tailStart = tailStart;
            Length = length;
        }

        public long Length { get; }

        public float this[long index] => index < _headCount ? _head[index] : _tail[index - _tailStart];
    }

    /// <summary>
    /// Per-hop energy of the broadband signal and of its 40–150 Hz band (kick / bass), which
    /// the onset envelopes, bar accents and outro all read; fed one hop at a time, in order.
    /// </summary>
    private sealed class HopEnergies
    {
        private readonly Biquad _hp;
        private readonly Biquad _lp;
        private readonly List<float> _broadband;
        private readonly List<float> _lowBand;
        private double _h1;
        private double _h2;
        private double _l1;
        private double _l2;
        private double _total;

        public HopEnergies(int sampleRate, int capacity)
        {
            var lowRate = sampleRate / (double)LowBandDecimation;
            _hp = Biquad.HighPass(LowBandLowHz, lowRate);
            _lp = Biquad.LowPass(LowBandHighHz, lowRate);
            _broadband = new List<float>(capacity);
            _lowBand = new List<float>(capacity);
        }

        public double MeanSquare => _broadband.Count == 0 ? 0 : _total / ((double)_broadband.Count * Hop);

        public float[] Broadband() => _broadband.ToArray();

        public float[] LowBand() => _lowBand.ToArray();

        public void Add(ReadOnlySpan<float> hop)
        {
            var hp = _hp;
            var lp = _lp;
            double h1 = _h1, h2 = _h2, l1 = _l1, l2 = _l2;
            var sb = SumOfSquares(hop);
            double sl = 0;
            for (var g = 0; g < Hop; g += LowBandDecimation)
            {
                double x = ((hop[g] + hop[g + 1]) + (hop[g + 2] + hop[g + 3]) + ((hop[g + 4] + hop[g + 5]) + (hop[g + 6] + hop[g + 7]))) / LowBandDecimation;

                // Transposed direct form II, high-pass into low-pass.
                var y = (hp.B0 * x) + h1;
                h1 = (hp.B1 * x) - (hp.A1 * y) + h2;
                h2 = (hp.B2 * x) - (hp.A2 * y);
                var z = (lp.B0 * y) + l1;
                l1 = (lp.B1 * y) - (lp.A1 * z) + l2;
                l2 = (lp.B2 * y) - (lp.A2 * z);
                sl += z * z;
            }

            _broadband.Add(sb);

            // Scaled back to full-rate energy, so both bands read on one scale.
            _lowBand.Add((float)(sl * LowBandDecimation));
            _total += sb;

            // Flush decaying filter state in silence: denormal arithmetic is ~100× slower on x86.
            if (Math.Abs(h1) < 1e-25 && Math.Abs(h2) < 1e-25)
            {
                h1 = h2 = 0;
            }

            if (Math.Abs(l1) < 1e-25 && Math.Abs(l2) < 1e-25)
            {
                l1 = l2 = 0;
            }

            (_h1, _h2, _l1, _l2) = (h1, h2, l1, l2);
        }
    }
}

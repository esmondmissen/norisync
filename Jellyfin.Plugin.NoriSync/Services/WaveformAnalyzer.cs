using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FftSharp;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Produces a compact per-track waveform byte stream for UI animation. Output format
/// is the LocalTracks v6 analyzer: a 4-byte magic header followed by N frames of 4
/// bytes each at <see cref="StorageHz"/>. Each frame is a big-endian 32-bit word
/// packed as [kick:1 | onset0..4:5 | b0:4 | b1:4 | b2:4 | b3:4 | b4:4 | reserved:6]
/// — kick is the global HPSS kick-onset flag, the five onset bits are per-band
/// transient flags, and bars are 4-bit magnitudes (0..15).
/// </summary>
public static class WaveformAnalyzer
{
    /// <summary>
    /// Canonical full-pipeline sample rate. Used by the full-process path because the same
    /// buffer is also resampled to CLAP's 48 kHz via in-process polyphase — a 44.1→48 ratio
    /// is the cheapest source rate for that step.
    /// </summary>
    public const int SampleRate = 44100;

    /// <summary>
    /// Cache-invalidation tag persisted alongside each waveform blob. Bump when the algorithm
    /// or output format changes so stale rows get re-analyzed.
    /// </summary>
    public const string Version = "waveform-v5.6";

    /// <summary>
    /// Version string of the immediately-preceding analyzer, which emitted the same per-frame
    /// packing at 60 Hz. Rows at this version can be downsampled to <see cref="Version"/> in
    /// place via <see cref="ConvertLegacyToCurrent"/> — no audio re-decode required.
    /// </summary>
    public const string LegacyHalfRateVersion = "waveform-v5.5";

    /// <summary>
    /// 4-byte prefix written to every blob: "LT" + format version + reserved. Consumers
    /// reject any payload that doesn't start with this, which forces re-analysis of
    /// older waveform blobs after a format bump. Version byte bumped to 6 when storage
    /// dropped from 60Hz to 30Hz, so clients can distinguish frame timing.
    /// </summary>
    public static readonly byte[] MagicHeader = { 0x4C, 0x54, 0x06, 0x00 };

    private const float AnalysisHz = 60f;
    private const float StorageHz = 30f;
    private const int BandCount = 5;

    // Kick + 5 bar values carried through Pass 3 per analysis frame. Per-band onset
    // flags travel alongside in a parallel bool[][] rather than padding this array.
    private const int ValuesPerFrame = 6;

    // FFT size is rate-derived (see PickFftSize) so the analysis window stays roughly
    // 60–95 ms regardless of input rate. At 44.1 k this resolves to 4096 (the original
    // tuning); at 16 k it drops to 1024, ~4× cheaper per FFT.

    // Median filter kernels for HPSS (harmonic-percussive separation).
    private const int HarmonicKernelSize = 31;
    private const int PercussiveKernelSize = 17;

    // Only the low bins are kept for HPSS — the kick lives in the first ~210Hz, and we only
    // need bins up to KickHighBinCap+8 so the vertical median (kernel 17) around bin 11 is
    // well-defined. The Swift reference stored 50 bins but never read above bin 11.
    private const int HpssBinCount = 20;

    // Kick onset detection tunables.
    private const int KickLowBin = 2;
    private const int KickHighBinCap = 12;
    private const int FluxHistorySize = 10;
    private const float KickSensitivity = 1.25f;
    private const float KickMinThreshold = 0.015f;

    // Threshold applied to normalized kick magnitude when packing the 1-bit flag. Tuned
    // low so weak kicks still trigger — missing a kick is visually worse than over-firing.
    private const float KickBitThreshold = 0.25f;

    // Per-band onset detection. Cheaper than HPSS — raw band-energy spectral flux against
    // a rolling median. Used purely for visual sparkle, so a few false positives are fine.
    private const int BandOnsetHistorySize = 10;
    private const float BandOnsetSensitivity = 1.5f;
    private const float BandOnsetMinThreshold = 0.02f;

    // 4-bit bar quantization: magnitudes 0..1 map to integers 0..15.
    private const float BarLevelsMax = 15f;

    // Contrast enhancement (Pass 3).
    private const float GateThreshold = 0.02f;
    private const float GlobalGain = 4.0f;
    private const float FallbackPowerCurve = 2.0f;

    // Professional instrument-targeted bands (Hz):
    //   0: Low bass / kick body
    //   1: Upper bass / warmth
    //   2: Mids — vocals, snare body, guitars
    //   3: Highs — snare snap, presence, leads
    //   4: Air — cymbals, hi-hats, openness
    private static readonly (float Min, float Max)[] BandRanges =
    {
        (60f, 110f),
        (110f, 250f),
        (250f, 900f),
        (900f, 2000f),
        (2000f, 6000f)
    };

    // Spectral tilt compensation — boosts higher bands so they show alongside bass energy.
    private static readonly float[] FrequencyWeights = { 1.0f, 1.0f, 1.6f, 2.4f, 4.0f };

    // 4-byte magic header of the legacy 60 Hz (v5) blob format, accepted by ConvertLegacyToCurrent.
    private static readonly byte[] LegacyMagicHeader = { 0x4C, 0x54, 0x05, 0x00 };

    /// <summary>
    /// Analyzes mono PCM samples and returns a packed blob: <see cref="MagicHeader"/>
    /// followed by 4 bytes per storage frame at <see cref="StorageHz"/>. Returns
    /// <see langword="null"/> for inputs shorter than a single FFT window.
    /// </summary>
    /// <param name="samples">Mono PCM samples.</param>
    /// <param name="sampleRate">Sample rate of <paramref name="samples"/> in Hz. Bin widths and
    /// FFT window sizing are derived from this so any rate ≥ 12 kHz produces a comparable result.</param>
    /// <returns>Packed waveform bytes, or null when the input is too short.</returns>
    public static byte[]? Analyze(float[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var fftSize = PickFftSize(sampleRate);
        if (samples.Length < fftSize)
        {
            return null;
        }

        // Float-spaced frame stride preserves an exact 60 fps output rate for any input
        // rate (e.g. 16000/60 = 266.67 — the int cast on each frame's startSample averages
        // out to 60.0 fps). At 44.1 k this is exactly 735.0, matching the old int math.
        var samplesPerFrame = sampleRate / AnalysisHz;
        var totalFrames = (int)(samples.Length / samplesPerFrame);
        if (totalFrames <= 0)
        {
            return null;
        }

        var window = new double[fftSize];
        FillHannWindow(window);

        var allBandEnergies = new float[totalFrames][];
        var spectrogram = new float[totalFrames][];
        for (var i = 0; i < totalFrames; i++)
        {
            allBandEnergies[i] = new float[BandCount];
            spectrogram[i] = new float[HpssBinCount];
        }

        var nyquist = sampleRate / 2f;
        var binWidth = nyquist / (fftSize / 2f);
        var fftScale = 1.0 / fftSize;

        // Only magnitudes below the top band's max frequency are ever read — skip the rest.
        var maxNeededBin = Math.Min(fftSize / 2, (int)(BandRanges[BandCount - 1].Max / binWidth) + 2);

        // ===== Pass 1: windowed FFT per frame → per-band energy + low-bin spectrogram =====
        // Parallelized across frames. Each thread gets its own FFT scratch buffers.
        Parallel.For(
            0,
            totalFrames,
            () => (FrameBuffer: new double[fftSize], Magnitudes: new float[maxNeededBin]),
            (frameIdx, _, local) =>
            {
                var frameBuffer = local.FrameBuffer;
                var magnitudes = local.Magnitudes;

                var startSample = (int)(frameIdx * samplesPerFrame);

                // Center the FFT window on the frame, clamped to buffer bounds.
                var centerOffset = ((int)samplesPerFrame - fftSize) / 2;
                var idealReadStart = startSample + centerOffset;
                var readStart = Math.Max(0, Math.Min(idealReadStart, samples.Length - fftSize));

                for (var i = 0; i < fftSize; i++)
                {
                    frameBuffer[i] = samples[readStart + i] * window[i];
                }

                var spectrum = FFT.Forward(frameBuffer);

                for (var b = 0; b < magnitudes.Length; b++)
                {
                    magnitudes[b] = (float)(spectrum[b].Magnitude * fftScale);
                }

                var bins = magnitudes.Length > HpssBinCount ? HpssBinCount : magnitudes.Length;
                var spectRow = spectrogram[frameIdx];
                for (var b = 0; b < bins; b++)
                {
                    spectRow[b] = magnitudes[b];
                }

                var bandRow = allBandEnergies[frameIdx];
                for (var bandIdx = 0; bandIdx < BandCount; bandIdx++)
                {
                    var range = BandRanges[bandIdx];
                    var lowerBin = (int)(range.Min / binWidth);
                    var upperBin = (int)(range.Max / binWidth);
                    var safeLower = Math.Max(0, Math.Min(lowerBin, magnitudes.Length - 1));
                    var safeUpper = Math.Max(safeLower + 1, Math.Min(upperBin, magnitudes.Length));

                    float peak = 0f;
                    float sumSquares = 0f;
                    var count = safeUpper - safeLower;
                    for (var b = safeLower; b < safeUpper; b++)
                    {
                        var m = magnitudes[b];
                        if (m > peak)
                        {
                            peak = m;
                        }

                        sumSquares += m * m;
                    }

                    var rms = count > 0 ? MathF.Sqrt(sumSquares / count) : 0f;
                    // Peak captures transient snap, RMS captures sustained body.
                    var bandEnergy = (0.6f * peak) + (0.4f * rms);
                    bandEnergy *= FrequencyWeights[bandIdx];

                    bandRow[bandIdx] = bandEnergy;
                }

                return local;
            },
            _ => { });

        // ===== HPSS: harmonic-percussive separation on the low-bin spectrogram =====
        var percussive = PerformHpssPercussive(spectrogram, totalFrames, HpssBinCount);

        // ===== Kick detection: spectral flux on sub-bass of the percussive spectrogram =====
        var kickEnergies = ExtractKickEnergies(percussive, totalFrames, HpssBinCount);

        // ===== Per-band onsets: band-energy spectral flux per band for visual sparkle =====
        var perBandOnsets = ExtractPerBandOnsets(allBandEnergies, totalFrames);

        // ===== Pass 2: per-band max (band 0 independent, bands 1–4 share a max) =====
        var maxPerBand = new float[BandCount];
        Array.Fill(maxPerBand, 0.0001f);

        for (var i = 0; i < totalFrames; i++)
        {
            if (allBandEnergies[i][0] > maxPerBand[0])
            {
                maxPerBand[0] = allBandEnergies[i][0];
            }
        }

        var maxKick = 0.0001f;
        for (var i = 0; i < totalFrames; i++)
        {
            if (kickEnergies[i] > maxKick)
            {
                maxKick = kickEnergies[i];
            }
        }

        var sharedMax = 0.0001f;
        for (var i = 0; i < totalFrames; i++)
        {
            var row = allBandEnergies[i];
            for (var b = 1; b < BandCount; b++)
            {
                if (row[b] > sharedMax)
                {
                    sharedMax = row[b];
                }
            }
        }

        for (var b = 1; b < BandCount; b++)
        {
            maxPerBand[b] = sharedMax;
        }

        // ===== Pass 2.5: adaptive contrast curve from the track's dynamic range =====
        var adaptivePower = ComputeAdaptivePower(allBandEnergies, totalFrames);

        // ===== Pass 3: normalize + contrast. Working frame = [kick, b0, b1, b2, b3, b4] =====
        var normalized = new float[totalFrames][];
        for (var i = 0; i < totalFrames; i++)
        {
            var src = allBandEnergies[i];
            var outFrame = new float[ValuesPerFrame];

            var kickNormalized = kickEnergies[i] / MathF.Max(maxKick, 0.0001f);
            outFrame[0] = Contrast(kickNormalized, adaptivePower);

            for (var b = 0; b < BandCount; b++)
            {
                outFrame[b + 1] = Contrast(src[b] / maxPerBand[b], adaptivePower);
            }

            normalized[i] = outFrame;
        }

        // ===== Pass 4: downsample to StorageHz and pack into 2-byte frames =====
        var downsampleRatio = AnalysisHz / StorageHz;

        // Count output frames up-front so we can allocate exactly (mirrors the loop below).
        var outputFrames = 0;
        for (var probe = 0f; (int)probe < totalFrames; probe += downsampleRatio)
        {
            outputFrames++;
        }

        var output = new byte[MagicHeader.Length + (outputFrames * 4)];
        Buffer.BlockCopy(MagicHeader, 0, output, 0, MagicHeader.Length);
        var writeIdx = MagicHeader.Length;

        var maxVals = new float[ValuesPerFrame];
        var avgVals = new float[ValuesPerFrame];
        var onsetWindow = new bool[BandCount];

        var sourceIndex = 0f;
        while ((int)sourceIndex < totalFrames)
        {
            var startIdx = (int)sourceIndex;
            var endIdx = Math.Min((int)(sourceIndex + downsampleRatio), totalFrames);

            Array.Clear(maxVals);
            Array.Clear(avgVals);
            Array.Clear(onsetWindow);
            var count = 0;

            for (var i = startIdx; i < endIdx; i++)
            {
                var row = normalized[i];
                for (var v = 0; v < ValuesPerFrame; v++)
                {
                    if (row[v] > maxVals[v])
                    {
                        maxVals[v] = row[v];
                    }

                    avgVals[v] += row[v];
                }

                var onsetRow = perBandOnsets[i];
                for (var b = 0; b < BandCount; b++)
                {
                    if (onsetRow[b])
                    {
                        onsetWindow[b] = true;
                    }
                }

                count++;
            }

            var invCount = count > 0 ? 1f / count : 0f;

            // Kick preserves the transient via max. Bars use peak-or-avg blend.
            var kickFinal = maxVals[0];
            var b0 = BlendBarValue(maxVals[1], avgVals[1] * invCount);
            var b1 = BlendBarValue(maxVals[2], avgVals[2] * invCount);
            var b2 = BlendBarValue(maxVals[3], avgVals[3] * invCount);
            var b3 = BlendBarValue(maxVals[4], avgVals[4] * invCount);
            var b4 = BlendBarValue(maxVals[5], avgVals[5] * invCount);

            var word = PackFrame(kickFinal, onsetWindow, b0, b1, b2, b3, b4);
            output[writeIdx++] = (byte)(word >> 24);
            output[writeIdx++] = (byte)(word >> 16);
            output[writeIdx++] = (byte)(word >> 8);
            output[writeIdx++] = (byte)(word & 0xFF);

            sourceIndex += downsampleRatio;
        }

        return output;
    }

    /// <summary>
    /// Downsamples a legacy 60 Hz (v5) waveform blob to the current 30 Hz (v6) format by merging
    /// each consecutive pair of frames: the kick/onset flag bits are OR-ed and each 4-bit band
    /// magnitude takes the per-band maximum. This reproduces <see cref="Analyze"/>'s downsample
    /// pass exactly — bars are a peak (max), and 4-bit quantization is monotonic, so merging the
    /// already-quantized frames equals re-quantizing the merged source — so the result is
    /// bit-for-bit identical to re-analyzing the audio at 30 Hz, without decoding it again.
    /// </summary>
    /// <param name="blob">A waveform blob. Only blobs starting with the legacy v5 magic header
    /// are converted.</param>
    /// <returns>The converted v6 blob, or <see langword="null"/> when <paramref name="blob"/> is
    /// not a recognised v5 blob (caller should fall back to re-analysis).</returns>
    public static byte[]? ConvertLegacyToCurrent(byte[]? blob)
    {
        const int headerLen = 4;
        if (blob == null || blob.Length < headerLen || (blob.Length - headerLen) % 4 != 0)
        {
            return null;
        }

        for (var i = 0; i < headerLen; i++)
        {
            if (blob[i] != LegacyMagicHeader[i])
            {
                return null;
            }
        }

        var frameCount = (blob.Length - headerLen) / 4;
        var outFrames = (frameCount + 1) / 2;
        var output = new byte[MagicHeader.Length + (outFrames * 4)];
        Buffer.BlockCopy(MagicHeader, 0, output, 0, MagicHeader.Length);

        var writeIdx = MagicHeader.Length;
        for (var k = 0; k < outFrames; k++)
        {
            var a = ReadWord(blob, headerLen + (2 * k * 4));
            // Odd tail: a singleton window. Merging a frame with itself is a no-op (OR/max),
            // so reuse `a` and the merge yields `a` unchanged — matching the analyzer's
            // single-frame final window.
            var b = (2 * k) + 1 < frameCount ? ReadWord(blob, headerLen + (((2 * k) + 1) * 4)) : a;
            var merged = MergeFrames(a, b);

            output[writeIdx++] = (byte)(merged >> 24);
            output[writeIdx++] = (byte)(merged >> 16);
            output[writeIdx++] = (byte)(merged >> 8);
            output[writeIdx++] = (byte)(merged & 0xFF);
        }

        return output;
    }

    private static uint ReadWord(byte[] data, int offset) =>
        ((uint)data[offset] << 24)
        | ((uint)data[offset + 1] << 16)
        | ((uint)data[offset + 2] << 8)
        | data[offset + 3];

    // Combines two packed frames the way Pass 4 combines a window: kick (bit 31) and the five
    // per-band onset flags (bits 30–26) are OR-ed; each 4-bit band magnitude (shifts 22,18,14,
    // 10,6) takes the per-band maximum. Reserved bits stay zero.
    private static uint MergeFrames(uint a, uint b)
    {
        var word = (a | b) & 0xFC000000u;
        for (var shift = 22; shift >= 6; shift -= 4)
        {
            var na = (a >> shift) & 0xFu;
            var nb = (b >> shift) & 0xFu;
            word |= (na > nb ? na : nb) << shift;
        }

        return word;
    }

    // Power-of-2 FFT size that targets a 60–95 ms analysis window across plausible
    // input rates. 4096 at 44.1 k matches the original tuning bit-for-bit.
    private static int PickFftSize(int sampleRate)
    {
        if (sampleRate >= 36000)
        {
            return 4096;
        }

        if (sampleRate >= 24000)
        {
            return 2048;
        }

        return 1024;
    }

    private static float Contrast(float normalized, float power)
    {
        var amplified = MathF.Min(1f, normalized * GlobalGain);
        var gated = MathF.Max(0f, amplified - GateThreshold);
        var rescaled = gated / (1f - GateThreshold);
        return MathF.Pow(MathF.Min(1f, rescaled), power);
    }

    private static float BlendBarValue(float peak, float avg)
    {
        // Always favor the peak — at AnalysisHz == StorageHz this is a no-op (peak == avg),
        // and at fractional ratios the transient-preserving choice is what reads as alive.
        _ = avg;
        return peak;
    }

    private static uint PackFrame(float kick, bool[] onsets, float b0, float b1, float b2, float b3, float b4)
    {
        var kickBit = kick >= KickBitThreshold ? 1u : 0u;
        var onsetBits = 0u;
        for (var i = 0; i < BandCount; i++)
        {
            if (onsets[i])
            {
                // band 0 → bit 30, band 4 → bit 26
                onsetBits |= 1u << (30 - i);
            }
        }

        return (kickBit << 31)
            | onsetBits
            | ((uint)Quantize(b0) << 22)
            | ((uint)Quantize(b1) << 18)
            | ((uint)Quantize(b2) << 14)
            | ((uint)Quantize(b3) << 10)
            | ((uint)Quantize(b4) << 6);
    }

    private static int Quantize(float v)
    {
        var clamped = MathF.Max(0f, MathF.Min(1f, v));
        return (int)MathF.Round(clamped * BarLevelsMax);
    }

    private static void FillHannWindow(double[] buffer)
    {
        // Hann window matching Apple's vDSP_HANN_NORM normalization used in the iOS client.
        // vDSP_HANN_NORM scales so the sum of squared coefficients equals N/2.
        var n = buffer.Length;
        double sumSq = 0;
        for (var i = 0; i < n; i++)
        {
            var w = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1)));
            buffer[i] = w;
            sumSq += w * w;
        }

        var targetSumSq = n / 2.0;
        if (sumSq > 0)
        {
            var norm = Math.Sqrt(targetSumSq / sumSq);
            for (var i = 0; i < n; i++)
            {
                buffer[i] *= norm;
            }
        }
    }

    private static float[][] PerformHpssPercussive(float[][] spectrogram, int frameCount, int binCount)
    {
        var harmonic = new float[frameCount][];
        var percussive = new float[frameCount][];
        for (var i = 0; i < frameCount; i++)
        {
            harmonic[i] = new float[binCount];
            percussive[i] = new float[binCount];
        }

        // Horizontal (time-axis) median filter → harmonic estimate. Parallel across bins —
        // each bin writes a different column, no sharing across threads.
        Parallel.For(
            0,
            binCount,
            () => new float[HarmonicKernelSize],
            (bin, _, hWindow) =>
            {
                for (var frame = 0; frame < frameCount; frame++)
                {
                    var start = Math.Max(0, frame - (HarmonicKernelSize / 2));
                    var end = Math.Min(frameCount, frame + (HarmonicKernelSize / 2) + 1);
                    var len = end - start;
                    for (var i = 0; i < len; i++)
                    {
                        hWindow[i] = spectrogram[start + i][bin];
                    }

                    harmonic[frame][bin] = Median(hWindow, len);
                }

                return hWindow;
            },
            _ => { });

        // Vertical (frequency-axis) median filter → percussive estimate. Parallel across
        // frames — each frame row is written independently.
        Parallel.For(
            0,
            frameCount,
            () => new float[PercussiveKernelSize],
            (frame, _, pWindow) =>
            {
                var row = spectrogram[frame];
                for (var bin = 0; bin < binCount; bin++)
                {
                    var start = Math.Max(0, bin - (PercussiveKernelSize / 2));
                    var end = Math.Min(binCount, bin + (PercussiveKernelSize / 2) + 1);
                    var len = end - start;
                    for (var i = 0; i < len; i++)
                    {
                        pWindow[i] = row[start + i];
                    }

                    percussive[frame][bin] = Median(pWindow, len);
                }

                return pWindow;
            },
            _ => { });

        // Soft Wiener-like mask — split the original energy between harmonic and percussive.
        for (var frame = 0; frame < frameCount; frame++)
        {
            var specRow = spectrogram[frame];
            var harmRow = harmonic[frame];
            var percRow = percussive[frame];
            for (var bin = 0; bin < binCount; bin++)
            {
                var h = harmRow[bin];
                var p = percRow[bin];
                var total = h + p + 1e-5f;
                var pMask = p / total;
                percRow[bin] = specRow[bin] * pMask;
            }
        }

        return percussive;
    }

    private static float[] ExtractKickEnergies(float[][] percussive, int frameCount, int binCount)
    {
        var kick = new float[frameCount];
        var highBin = Math.Min(KickHighBinCap, binCount);
        var denom = (float)(highBin - KickLowBin);
        if (denom <= 0f)
        {
            return kick;
        }

        var fluxHistory = new float[FluxHistorySize];
        var fluxHistoryCount = 0;
        var fluxWorkspace = new float[FluxHistorySize];
        var prevSubBass = 0f;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var row = percussive[frame];

            // Low-frequency percussive energy in a narrow "kick knock" band.
            var sum = 0f;
            for (var bin = KickLowBin; bin < highBin; bin++)
            {
                sum += row[bin];
            }

            var subBass = sum / denom;

            // Positive spectral flux = onset strength.
            var flux = MathF.Max(0f, subBass - prevSubBass);

            if (fluxHistoryCount < FluxHistorySize)
            {
                fluxHistory[fluxHistoryCount++] = flux;
            }
            else
            {
                for (var i = 0; i < FluxHistorySize - 1; i++)
                {
                    fluxHistory[i] = fluxHistory[i + 1];
                }

                fluxHistory[FluxHistorySize - 1] = flux;
            }

            Array.Copy(fluxHistory, fluxWorkspace, fluxHistoryCount);
            var medianFlux = Median(fluxWorkspace, fluxHistoryCount);
            var dynamicThreshold = (medianFlux * KickSensitivity) + KickMinThreshold;

            kick[frame] = flux > dynamicThreshold ? subBass : 0f;
            prevSubBass = subBass;
        }

        return kick;
    }

    private static bool[][] ExtractPerBandOnsets(float[][] bandEnergies, int frameCount)
    {
        var onsets = new bool[frameCount][];
        for (var i = 0; i < frameCount; i++)
        {
            onsets[i] = new bool[BandCount];
        }

        // Each band runs an independent rolling-median spectral-flux detector. Bands are
        // independent so we parallelize across them; per-band state stays serial in time.
        Parallel.For(0, BandCount, band =>
        {
            var fluxHistory = new float[BandOnsetHistorySize];
            var fluxWorkspace = new float[BandOnsetHistorySize];
            var fluxHistoryCount = 0;
            var prevEnergy = 0f;

            for (var frame = 0; frame < frameCount; frame++)
            {
                var energy = bandEnergies[frame][band];
                var flux = MathF.Max(0f, energy - prevEnergy);

                if (fluxHistoryCount < BandOnsetHistorySize)
                {
                    fluxHistory[fluxHistoryCount++] = flux;
                }
                else
                {
                    for (var i = 0; i < BandOnsetHistorySize - 1; i++)
                    {
                        fluxHistory[i] = fluxHistory[i + 1];
                    }

                    fluxHistory[BandOnsetHistorySize - 1] = flux;
                }

                Array.Copy(fluxHistory, fluxWorkspace, fluxHistoryCount);
                var medianFlux = Median(fluxWorkspace, fluxHistoryCount);
                var threshold = (medianFlux * BandOnsetSensitivity) + BandOnsetMinThreshold;

                onsets[frame][band] = flux > threshold;
                prevEnergy = energy;
            }
        });

        return onsets;
    }

    private static float ComputeAdaptivePower(float[][] allBandEnergies, int frameCount)
    {
        // Skip band 0 — it has its own normalization stream via the kick channel.
        var total = frameCount * (BandCount - 1);
        if (total <= 10)
        {
            return FallbackPowerCurve;
        }

        var flat = new float[total];
        var idx = 0;
        for (var i = 0; i < frameCount; i++)
        {
            var row = allBandEnergies[i];
            for (var b = 1; b < BandCount; b++)
            {
                flat[idx++] = row[b];
            }
        }

        Array.Sort(flat);
        var p90 = flat[(int)(flat.Length * 0.9f)];
        var p50 = MathF.Max(flat[flat.Length / 2], 0.0001f);
        var dynamicRatio = p90 / p50;

        // Map ratio 1.0–6.0 → power 1.2–2.5. Compressed tracks get a gentler curve; dynamic
        // tracks get more punch.
        return MathF.Min(2.5f, MathF.Max(1.2f, 1.2f + ((dynamicRatio - 1.0f) * 0.26f)));
    }

    private static float Median(float[] buffer, int length)
    {
        if (length <= 0)
        {
            return 0f;
        }

        Array.Sort(buffer, 0, length);
        var mid = length / 2;
        return (length % 2 == 0)
            ? (buffer[mid - 1] + buffer[mid]) / 2f
            : buffer[mid];
    }
}

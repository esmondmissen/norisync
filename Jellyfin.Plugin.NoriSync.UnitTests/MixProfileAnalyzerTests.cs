using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.NoriSync.Models;
using Jellyfin.Plugin.NoriSync.Services;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.NoriSync.UnitTests;

/// <summary>
/// <see cref="MixProfileAnalyzer"/> against synthetic tracks whose beats are known to the
/// sample: tempo, lattice phase, where the beats start and stop, bar phase, outro, and the
/// 48-byte <c>mixprofile-v1</c> layout the app pins (API_FORMAT §4.11).
/// </summary>
public class MixProfileAnalyzerTests
{
    private const int Sr = 44100;
    private const double PhaseTolerance = 0.002;
    private const double TempoTolerance = 0.0005;

    private readonly ITestOutputHelper _output;

    public MixProfileAnalyzerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SteadyTrack_HeadAndTail_FindTempoAndPhase()
    {
        var period = 60.0 / 123;
        var track = new Track(200);
        track.Pad(0, 200, 0.05);
        var beats = track.Beats(0.1234, 200, period);
        track.Kicks(beats, 0.6);
        track.Hats(beats.Select(b => b + (period / 2)), 0.08);

        var profile = Analyze(track);

        var head = AssertRegion(profile.Head, period, beats);
        var tail = AssertRegion(profile.Tail, period, beats);
        AssertNear(beats[0], head.FirstBeatSeconds, PhaseTolerance, "head first beat");
        AssertNear(beats[^1], tail.LastBeatSeconds, PhaseTolerance, "tail last beat");
        Assert.True(head.Confidence >= 230, $"head confidence {head.Confidence}");
        Assert.True(tail.Confidence >= 230, $"tail confidence {tail.Confidence}");

        // Even kicks give no bar phase, and a track that ends on its beat doesn't recede.
        Assert.False(head.DownbeatConfident);
        Assert.Null(profile.OutroRecedesAt);
    }

    [Fact]
    public void BeatlessIntro_FirstBeatIsWhereTheKicksStart()
    {
        var period = 60.0 / 126;
        var track = new Track(180);
        track.Pad(0, 180, 0.08);
        var beats = track.Beats(20.0573, 180, period);
        track.Kicks(beats, 0.6);

        var profile = Analyze(track);

        var head = AssertRegion(profile.Head, period, beats);
        AssertNear(beats[0], head.FirstBeatSeconds, PhaseTolerance, "head first beat");
        Assert.InRange(head.ReferenceBeatSeconds, 20.0, 60.0);
    }

    [Fact]
    public void BeatlessFadeOutOutro_LastBeatAndOutroRecedesAt()
    {
        var period = 60.0 / 118;
        var track = new Track(180);
        var beats = track.Beats(0.31, 150, period);
        track.Kicks(beats, 0.6);
        track.Pad(0, 150, 0.05);
        track.Pad(150, 180, 0.05, fadeTo: 0);

        var profile = Analyze(track);

        var tail = AssertRegion(profile.Tail, period, beats);
        AssertNear(beats[^1], tail.LastBeatSeconds, PhaseTolerance, "tail last beat");
        Assert.NotNull(profile.OutroRecedesAtSeconds);
        Assert.InRange(profile.OutroRecedesAtSeconds!.Value, beats[^1], beats[^1] + 0.6);
    }

    [Fact]
    public void TempoChange_HeadAndTailCarryTheirOwnPeriods()
    {
        var track = new Track(200);
        track.Pad(0, 200, 0.05);
        var slow = track.Beats(0.05, 100, 0.5);
        var fast = track.Beats(slow[^1] + 0.5, 200, 60.0 / 128);
        track.Kicks(slow, 0.6);
        track.Kicks(fast, 0.6);

        var profile = Analyze(track);

        AssertRegion(profile.Head, 0.5, slow);
        AssertRegion(profile.Tail, 60.0 / 128, fast);
    }

    [Theory]
    [InlineData(60.0, 120.0)]
    [InlineData(180.0, 90.0)]
    [InlineData(72.0, 144.0)]
    public void HalfOrDoubleTempo_FoldsIntoEightyToOneSixty(double trackBpm, double expectedBpm)
    {
        var track = new Track(150);
        track.Pad(0, 150, 0.03);
        var beats = track.Beats(0.217, 150, 60.0 / trackBpm);
        track.Kicks(beats, 0.6);

        var profile = Analyze(track);

        // Doubling puts lattice beats between the kicks; halving keeps every other kick.
        foreach (var region in new[] { profile.Head, profile.Tail })
        {
            Assert.NotNull(region);
            AssertNear(60.0 / expectedBpm, region!.PeriodSeconds, 60.0 / expectedBpm * TempoTolerance, "folded period");
            AssertNear(Nearest(beats, region.ReferenceBeatSeconds), region.ReferenceBeatSeconds, PhaseTolerance, "reference beat");
            AssertNear(Nearest(beats, region.FirstBeatSeconds), region.FirstBeatSeconds, PhaseTolerance, "first beat");
            AssertNear(Nearest(beats, region.LastBeatSeconds), region.LastBeatSeconds, PhaseTolerance, "last beat");
        }
    }

    [Fact]
    public void AccentedOne_GivesAConfidentDownbeat()
    {
        var period = 60.0 / 124;
        var track = new Track(150);
        track.Pad(0, 150, 0.04);
        var beats = track.Beats(0.402, 150, period);
        for (var j = 0; j < beats.Count; j++)
        {
            // Beat 1 of every bar is the third kick (index 2, 6, 10…), twice as loud.
            track.Kick(beats[j], j % 4 == 2 ? 0.8 : 0.35);
        }

        var profile = Analyze(track);

        foreach (var region in new[] { profile.Head!, profile.Tail! })
        {
            Assert.True(region.DownbeatConfident, $"downbeat confidence {region.DownbeatConfidence}");
            var referenceIndex = beats.IndexOf(beats.OrderBy(b => Math.Abs(b - region.ReferenceBeatSeconds)).First());
            Assert.Equal((((2 - referenceIndex) % 4) + 4) % 4, region.DownbeatOffset);
        }
    }

    [Fact]
    public void HumanTimingJitter_StillLocksToTheGrid()
    {
        var period = 60.0 / 97;
        var track = new Track(150);
        track.Pad(0, 150, 0.05);
        var grid = track.Beats(1.003, 150, period);
        var random = new Random(77);
        track.Kicks(grid.Select(t => t + ((random.NextDouble() - 0.5) * 0.008)), 0.6);

        var profile = Analyze(track);

        AssertRegion(profile.Head, period, grid);
        AssertRegion(profile.Tail, period, grid);
        Assert.True(profile.Head!.Confidence >= 200, $"head confidence {profile.Head.Confidence}");
    }

    [Fact]
    public void DenseMix_LocksToTheKicksNotTheOffbeatBass()
    {
        var period = 60.0 / 126;
        var track = new Track(180);
        track.Pad(0, 180, 0.05);
        var beats = track.Beats(0.611, 180, period);
        track.Kicks(beats, 0.5);
        track.Hats(beats.SelectMany(b => new[] { b, b + (period / 4), b + (period / 2), b + (3 * period / 4) }), 0.05);
        track.Hats(beats.Where((_, j) => j % 2 == 1), 0.25);
        track.Bass(beats.Select(b => b + (period / 2)), period / 2 * 0.8, 0.3);

        var profile = Analyze(track);

        AssertRegion(profile.Head, period, beats);
        AssertRegion(profile.Tail, period, beats);
    }

    [Fact]
    public void DriftingTempo_IsNotAConfidentLattice()
    {
        // 120 → 126 BPM across the track: no steady lattice holds ±15 ms over a region.
        var track = new Track(180);
        track.Pad(0, 180, 0.05);
        var beats = new List<double>();
        for (var t = 0.25; t < 179.9; t += 60.0 / (120 + (6 * t / 180)))
        {
            beats.Add(t);
        }

        track.Kicks(beats, 0.6);

        var profile = Analyze(track);

        _output.WriteLine(Describe("head", profile.Head) + Environment.NewLine + Describe("tail", profile.Tail));
        foreach (var region in new[] { profile.Head, profile.Tail })
        {
            Assert.True(region == null || region.Confidence < 180, $"confidence {region?.Confidence}");
        }
    }

    /// <summary>
    /// A steady kick under louder accents grouped in threes of a half-beat (every 1.5 beats),
    /// as an arpeggio accents a 3:2 figure: the tempo is the kick's, never two-thirds of it.
    /// (A single-lag tempo pick and a beat tracker took Daft Punk's "Aerodynamic" this way:
    /// 81.9 BPM over its 122.9.)
    /// </summary>
    [Fact]
    public void ThreeTwoAccents_DoNotPullTheTempoToTwoThirds()
    {
        var period = 60.0 / 123;
        var track = new Track(200);
        var beats = track.Beats(0.31, 199, period);
        track.Kicks(beats, 0.35);
        var sixteenths = track.Beats(0.31, 199, period / 4);
        track.Plucks(sixteenths, 880, 0.08);
        track.Plucks(sixteenths.Where((_, i) => i % 6 == 0), 1320, 0.5);
        track.Pad(0, 200, 0.05);

        var profile = Analyze(track);

        _output.WriteLine(Describe("head", profile.Head) + Environment.NewLine + Describe("tail", profile.Tail));
        AssertRegion(profile.Head, period, beats);
        AssertRegion(profile.Tail, period, beats);
    }

    /// <summary>
    /// A steady tempo with a sparse kit — kick on 1 and 3, a soft snare on 2 and 4 — is a
    /// confident grid: beats without a big attack cost clarity, not the whole region.
    /// </summary>
    [Fact]
    public void SparseSteadyBeat_IsConfident()
    {
        var period = 60.0 / 118;
        var track = new Track(200);
        var beats = track.Beats(0.42, 199, period);
        track.Kicks(beats.Where((_, i) => i % 2 == 0), 0.6);
        track.Hats(beats.Where((_, i) => i % 2 == 1), 0.25);
        track.Pad(0, 200, 0.08);

        var profile = Analyze(track);

        _output.WriteLine(Describe("head", profile.Head) + Environment.NewLine + Describe("tail", profile.Tail));
        foreach (var region in new[] { profile.Head, profile.Tail })
        {
            AssertRegion(region, period, beats);
            Assert.True(region!.Confidence >= 153, $"confidence {region.Confidence} (the client's floor is 0.6 = 153)");
        }
    }

    /// <summary>
    /// Real recordings, opt-in: <c>NORISYNC_MIX_FIXTURES</c> names a directory holding
    /// <c>expected.txt</c> (lines <c>file.wav bpm</c>) and those files as 44.1 kHz mono float
    /// WAVs (<c>afconvert -f WAVE -d LEF32@44100 -c 1</c>). Every head and tail must find that
    /// tempo (±0.3 %) at the client's confidence floor. The set the comb-grid analyser was
    /// built against: Daft Punk "Aerodynamic" 122.9 (the beat tracker found no head and an
    /// 81.9 BPM tail), "Harder, Better, Faster, Stronger" 123.5 (no tail), "Crescendolls"
    /// 124.1, Gorillaz "Rock the House" 118.0 (neither region), Bloc Party "Colours" 115.0,
    /// Pnau "Free Tibet" 138.0. Audio can't live in the repo.
    /// </summary>
    [Fact]
    public void RealRecordings_FindTheirTempo()
    {
        var directory = Environment.GetEnvironmentVariable("NORISYNC_MIX_FIXTURES");
        if (string.IsNullOrEmpty(directory))
        {
            _output.WriteLine("NORISYNC_MIX_FIXTURES not set: skipped.");
            return;
        }

        foreach (var line in File.ReadAllLines(Path.Combine(directory, "expected.txt")))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var bpm = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var samples = ReadFloatWav(Path.Combine(directory, parts[0]));
            var profile = MixProfileAnalyzer.AnalyzeProfile(samples, Sr);
            _output.WriteLine($"{parts[0]}: " + Describe("head", profile?.Head) + " | " + Describe("tail", profile?.Tail));
            foreach (var region in new[] { profile?.Head, profile?.Tail })
            {
                Assert.NotNull(region);
                AssertNear(bpm, region!.BeatsPerMinute, bpm * 0.003, $"{parts[0]} tempo");
                Assert.True(region.Confidence >= 153, $"{parts[0]} confidence {region.Confidence}");
            }
        }
    }

    private static float[] ReadFloatWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var p = 12;
        while (p + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, p, 4);
            var length = BitConverter.ToInt32(bytes, p + 4);
            if (id == "data")
            {
                var samples = new float[length / 4];
                Buffer.BlockCopy(bytes, p + 8, samples, 0, samples.Length * 4);
                return samples;
            }

            p += 8 + length + (length & 1);
        }

        throw new InvalidDataException($"{path}: no data chunk");
    }

    [Fact]
    public void Silence_ReturnsNull()
    {
        Assert.Null(MixProfileAnalyzer.Analyze(new float[Sr * 120], Sr));
    }

    [Fact]
    public void TooShort_ReturnsNull()
    {
        var track = new Track(6);
        track.Kicks(track.Beats(0.1, 6, 0.5), 0.6);
        Assert.Null(MixProfileAnalyzer.Analyze(track.Samples, Sr));
    }

    [Fact]
    public void BeatlessButAudible_IsAProfileWithNoRegions()
    {
        var track = new Track(120);
        track.Pad(0, 120, 0.2);

        var profile = Analyze(track);

        _output.WriteLine(Describe("head", profile.Head) + Environment.NewLine + Describe("tail", profile.Tail));
        Assert.Null(profile.Head);
        Assert.Null(profile.Tail);
        var blob = MixProfileAnalyzer.Encode(profile);
        Assert.Equal(0, blob[3]);
        Assert.All(blob[4..44], b => Assert.Equal(0, b));
    }

    [Fact]
    public void ShortTrack_RegionsOverlapAndAgree()
    {
        var period = 60.0 / 100;
        var track = new Track(40);
        var beats = track.Beats(0.5, 40, period);
        track.Kicks(beats, 0.6);

        var profile = Analyze(track);

        var head = AssertRegion(profile.Head, period, beats);
        var tail = AssertRegion(profile.Tail, period, beats);
        Assert.Equal(head.FirstBeat, tail.FirstBeat, 20u);
        Assert.Equal(head.LastBeat, tail.LastBeat, 20u);
    }

    [Fact]
    public void Encode_WritesTheExactLittleEndianLayout()
    {
        var profile = new MixProfile
        {
            Head = new MixProfileRegion
            {
                PeriodMicros = 0x01020304,
                ReferenceBeat = 0x05060708,
                FirstBeat = 0x090A0B0C,
                LastBeat = 0x0D0E0F10,
                Confidence = 0x11,
                DownbeatOffset = 2,
                DownbeatConfidence = 0x13,
                DownbeatConfident = true
            },
            Tail = null,
            OutroRecedesAt = 0xA1B2C3D4
        };

        var blob = MixProfileAnalyzer.Encode(profile);

        Assert.Equal(48, blob.Length);
        Assert.Equal(new byte[] { 0x4D, 0x50, 0x01, 0x05 }, blob[..4]);
        Assert.Equal(
            new byte[] { 0x04, 0x03, 0x02, 0x01, 0x08, 0x07, 0x06, 0x05, 0x0C, 0x0B, 0x0A, 0x09, 0x10, 0x0F, 0x0E, 0x0D, 0x11, 0x02, 0x13, 0x00 },
            blob[4..24]);
        Assert.All(blob[24..44], b => Assert.Equal(0, b));
        Assert.Equal(new byte[] { 0xD4, 0xC3, 0xB2, 0xA1 }, blob[44..48]);

        Assert.Equal(profile, MixProfileAnalyzer.Decode(blob));
    }

    [Fact]
    public void Encode_NoOutroIsAllOnes_AndFlagsTrackEachRegion()
    {
        var region = new MixProfileRegion { PeriodMicros = 500_000, DownbeatOffset = MixProfileAnalyzer.UnknownDownbeat };
        var blob = MixProfileAnalyzer.Encode(new MixProfile { Tail = region });

        Assert.Equal(0x02, blob[3]);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, blob[44..48]);
        Assert.Equal(255, blob[24 + 17]);
        Assert.Null(MixProfileAnalyzer.Decode(blob)!.OutroRecedesAt);
    }

    [Fact]
    public void Decode_IsStrict()
    {
        var good = MixProfileAnalyzer.Encode(new MixProfile { OutroRecedesAt = 1 });
        Assert.NotNull(MixProfileAnalyzer.Decode(good));
        Assert.Null(MixProfileAnalyzer.Decode(good[..47]));
        Assert.Null(MixProfileAnalyzer.Decode(good.Concat(new byte[] { 0 }).ToArray()));

        var badMagic = (byte[])good.Clone();
        badMagic[1] = 0x51;
        Assert.Null(MixProfileAnalyzer.Decode(badMagic));

        var badFormat = (byte[])good.Clone();
        badFormat[2] = 0x02;
        Assert.Null(MixProfileAnalyzer.Decode(badFormat));
    }

    /// <summary>
    /// The contract blob the app pins: fixed field values,
    /// so it is exact on every platform. Changing it is a wire break.
    /// </summary>
    [Fact]
    public void GoldenBlob_ForFixedFields()
    {
        var profile = new MixProfile
        {
            // 124 BPM head, bar phase 3 (confident); first and last are 61 beats either side of the reference.
            Head = new MixProfileRegion
            {
                PeriodMicros = 483_871,
                ReferenceBeat = 301_234,
                FirstBeat = 6_073,
                LastBeat = 596_395,
                Confidence = 242,
                DownbeatOffset = 3,
                DownbeatConfidence = 180,
                DownbeatConfident = true
            },

            // 128 BPM tail, bar phase unknown; 118 beats before the reference, 72 after.
            Tail = new MixProfileRegion
            {
                PeriodMicros = 468_750,
                ReferenceBeat = 2_053_125,
                FirstBeat = 1_500_000,
                LastBeat = 2_390_625,
                Confidence = 200,
                DownbeatOffset = MixProfileAnalyzer.UnknownDownbeat,
                DownbeatConfidence = 0,
                DownbeatConfident = false
            },
            OutroRecedesAt = 2_401_000
        };

        var hex = Convert.ToHexString(MixProfileAnalyzer.Encode(profile));
        _output.WriteLine("mixprofile-v1 golden (fixed fields): " + hex);
        Assert.Equal(
            "4D500107" + "1F620700B2980400B9170000AB190900F203B400" + "0E27070005541F0060E31600617A2400C8FF0000" + "E8A22400",
            hex);
    }

    /// <summary>
    /// Prints the analyser's own blob for a fixed synthetic fixture (124 BPM kicks from
    /// 0.4567 s to 240 s with off-beat hats over a pad, the last 20 s a beatless fade). Asserted
    /// semantically, not byte-for-byte: libm rounding may move a 0.1 ms tick between platforms.
    /// </summary>
    [Fact]
    public void GoldenBlob_FromTheAnalyser()
    {
        var period = 60.0 / 124;
        var track = new Track(240);
        var beats = track.Beats(0.4567, 220, period);
        track.Kicks(beats, 0.6);
        track.Hats(beats.Select(b => b + (period / 2)), 0.08);
        track.Pad(0, 220, 0.05);
        track.Pad(220, 240, 0.05, fadeTo: 0);

        var blob = MixProfileAnalyzer.Analyze(track.Samples, Sr);
        Assert.NotNull(blob);
        var profile = MixProfileAnalyzer.Decode(blob!)!;
        _output.WriteLine("mixprofile-v1 from the analyser: " + Convert.ToHexString(blob!));
        _output.WriteLine(Describe("head", profile.Head) + Environment.NewLine + Describe("tail", profile.Tail));
        _output.WriteLine($"outroRecedesAt {profile.OutroRecedesAtSeconds:F4} s");

        AssertRegion(profile.Head, period, beats);
        AssertRegion(profile.Tail, period, beats);
        Assert.InRange(profile.OutroRecedesAtSeconds!.Value, beats[^1], beats[^1] + 0.6);
    }

    /// <summary>
    /// Cost next to the waveform pass on the same 4-minute decode (printed; the bound is loose).
    /// </summary>
    [Fact]
    public void Cost_IsSmallNextToTheWaveformPass()
    {
        var period = 60.0 / 124;
        var track = new Track(240);
        var beats = track.Beats(0.4567, 240, period);
        track.Kicks(beats, 0.6);
        track.Hats(beats.Select(b => b + (period / 2)), 0.08);
        track.Pad(0, 240, 0.05);

        MixProfileAnalyzer.Analyze(track.Samples, Sr);
        WaveformAnalyzer.Analyze(track.Samples, Sr);

        // The waveform pass is Parallel.For across cores, so compare CPU time, not wall time.
        var (mixWall, mixCpu) = Measure(() => MixProfileAnalyzer.Analyze(track.Samples, Sr));
        var (waveWall, waveCpu) = Measure(() => WaveformAnalyzer.Analyze(track.Samples, Sr));
        _output.WriteLine(
            $"4-min track: mixprofile {mixWall.TotalMilliseconds:F0} ms wall / {mixCpu.TotalMilliseconds:F0} ms CPU, " +
            $"waveform {waveWall.TotalMilliseconds:F0} ms wall / {waveCpu.TotalMilliseconds:F0} ms CPU ({mixCpu / waveCpu:P0} of its CPU)");
        Assert.True(mixCpu < waveCpu, $"mixprofile {mixCpu.TotalMilliseconds:F0} ms CPU vs waveform {waveCpu.TotalMilliseconds:F0} ms CPU");
    }

    private static (TimeSpan Wall, TimeSpan Cpu) Measure(Action action)
    {
        var bestWall = TimeSpan.MaxValue;
        var bestCpu = TimeSpan.MaxValue;
        var process = Process.GetCurrentProcess();
        for (var i = 0; i < 3; i++)
        {
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            action();
            watch.Stop();
            process.Refresh();
            var used = process.TotalProcessorTime - cpu;
            bestWall = watch.Elapsed < bestWall ? watch.Elapsed : bestWall;
            bestCpu = used < bestCpu ? used : bestCpu;
        }

        return (bestWall, bestCpu);
    }

    private static MixProfile Analyze(Track track)
    {
        var profile = MixProfileAnalyzer.AnalyzeProfile(track.Samples, Sr);
        Assert.NotNull(profile);

        // Every analysed profile must survive its own wire format exactly.
        Assert.Equal(profile, MixProfileAnalyzer.Decode(MixProfileAnalyzer.Encode(profile!)));
        return profile!;
    }

    private static MixProfileRegion AssertRegion(MixProfileRegion? region, double period, List<double> beats)
    {
        Assert.NotNull(region);
        AssertNear(period, region!.PeriodSeconds, period * TempoTolerance, "period");

        // The lattice lands on the kicks everywhere in the region, not just near its reference.
        foreach (var time in new[] { region.ReferenceBeatSeconds, region.FirstBeatSeconds, region.LastBeatSeconds })
        {
            AssertNear(Nearest(beats, time), time, PhaseTolerance, "lattice beat");
        }

        Assert.True(region.FirstBeat <= region.ReferenceBeat && region.ReferenceBeat <= region.LastBeat, "reference inside the span");
        return region;
    }

    private static void AssertNear(double expected, double actual, double tolerance, string what)
    {
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected:F5}, got {actual:F5} (±{tolerance:F5})");
    }

    private static double Nearest(List<double> beats, double time) => beats.OrderBy(b => Math.Abs(b - time)).First();

    private static string Describe(string name, MixProfileRegion? r) => r == null
        ? $"{name}: none"
        : $"{name}: {r.BeatsPerMinute:F3} BPM, ref {r.ReferenceBeatSeconds:F4} s, beats {r.FirstBeatSeconds:F4}–{r.LastBeatSeconds:F4} s, confidence {r.Confidence}, downbeat {r.DownbeatOffset} ({r.DownbeatConfidence}, confident {r.DownbeatConfident})";

    /// <summary>
    /// A synthetic mono 44.1 kHz track: kicks (a 150→50 Hz sweep with a 1 ms click), hats
    /// (high-passed noise bursts), a three-note pad above the low band, and a −60 dB noise floor.
    /// </summary>
    private sealed class Track
    {
        private readonly Random _random = new(1234);

        public Track(double seconds)
        {
            Samples = new float[(int)(seconds * Sr)];
            for (var i = 0; i < Samples.Length; i++)
            {
                Samples[i] = (float)(0.001 * ((_random.NextDouble() * 2) - 1));
            }
        }

        public float[] Samples { get; }

        public List<double> Beats(double first, double until, double period)
        {
            var beats = new List<double>();
            for (var t = first; t < until - 0.05; t += period)
            {
                beats.Add(t);
            }

            return beats;
        }

        public void Kicks(IEnumerable<double> times, double amplitude)
        {
            foreach (var t in times)
            {
                Kick(t, amplitude);
            }
        }

        public void Kick(double time, double amplitude)
        {
            var start = (int)Math.Round(time * Sr);
            var phase = 0.0;
            for (var i = 0; i < (int)(0.25 * Sr) && start + i < Samples.Length; i++)
            {
                var t = i / (double)Sr;
                phase += 2 * Math.PI * (50 + (100 * Math.Exp(-t / 0.03))) / Sr;
                var v = amplitude * Math.Exp(-t / 0.08) * Math.Sin(phase);
                if (i < 44)
                {
                    v += amplitude * 0.5 * ((_random.NextDouble() * 2) - 1) * (1 - (i / 44.0));
                }

                Samples[start + i] += (float)v;
            }
        }

        public void Plucks(IEnumerable<double> times, double frequency, double amplitude)
        {
            foreach (var time in times)
            {
                var start = (int)Math.Round(time * Sr);
                for (var i = 0; i < (int)(0.12 * Sr) && start + i < Samples.Length; i++)
                {
                    var t = i / (double)Sr;
                    var envelope = Math.Min(1, t / 0.002) * Math.Exp(-t / 0.05);
                    Samples[start + i] += (float)(amplitude * envelope
                        * (Math.Sin(2 * Math.PI * frequency * t) + (0.4 * Math.Sin(4 * Math.PI * frequency * t))));
                }
            }
        }

        public void Hats(IEnumerable<double> times, double amplitude)
        {
            foreach (var time in times)
            {
                var start = (int)Math.Round(time * Sr);
                var previous = 0.0;
                for (var i = 0; i < (int)(0.03 * Sr) && start + i < Samples.Length; i++)
                {
                    var noise = (_random.NextDouble() * 2) - 1;
                    Samples[start + i] += (float)(amplitude * Math.Exp(-i / (0.01 * Sr)) * (noise - previous));
                    previous = noise;
                }
            }
        }

        public void Bass(IEnumerable<double> times, double length, double amplitude)
        {
            foreach (var time in times)
            {
                var start = (int)Math.Round(time * Sr);
                for (var i = 0; i < (int)(length * Sr) && start + i < Samples.Length; i++)
                {
                    var t = i / (double)Sr;
                    var envelope = Math.Min(1, t / 0.005) * Math.Exp(-t / 0.2);
                    Samples[start + i] += (float)(amplitude * envelope * Math.Sin(2 * Math.PI * 65.4 * t));
                }
            }
        }

        public void Pad(double from, double to, double amplitude, double? fadeTo = null)
        {
            var start = (int)(from * Sr);
            var end = Math.Min(Samples.Length, (int)(to * Sr));
            for (var i = start; i < end; i++)
            {
                var t = i / (double)Sr;
                var level = fadeTo == null ? amplitude : amplitude + ((fadeTo.Value - amplitude) * (i - start) / (end - start));
                var chord = Math.Sin(2 * Math.PI * 440 * t) + Math.Sin(2 * Math.PI * 554.37 * t) + Math.Sin(2 * Math.PI * 659.26 * t);
                Samples[i] += (float)(level * chord / 3);
            }
        }
    }
}

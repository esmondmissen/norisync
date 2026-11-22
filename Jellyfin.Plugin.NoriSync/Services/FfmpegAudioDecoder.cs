using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NoriSync.Services;

/// <summary>
/// Decodes audio files to mono PCM float samples by shelling out to the ffmpeg binary
/// already shipped with Jellyfin.
/// </summary>
public class FfmpegAudioDecoder
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<FfmpegAudioDecoder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegAudioDecoder"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Jellyfin media encoder (provides the ffmpeg binary path).</param>
    /// <param name="logger">Logger instance.</param>
    public FfmpegAudioDecoder(IMediaEncoder mediaEncoder, ILogger<FfmpegAudioDecoder> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Decodes the given media file to mono 32-bit float PCM at the requested sample rate,
    /// handing the samples to <paramref name="onSamples"/> in pieces, in order, as ffmpeg
    /// produces them (on the calling task, so a slow consumer slows the decode). Never the
    /// whole track at once: held whole, a multi-hour mix ran the server out of memory.
    /// </summary>
    /// <param name="sourcePath">The absolute path of the audio file to decode.</param>
    /// <param name="targetSampleRate">Target sample rate in Hz.</param>
    /// <param name="onSamples">Receives each piece: the buffer and how many samples it holds
    /// (the buffer is reused for the next piece).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of samples decoded.</returns>
    public async Task<long> DecodeAsync(string sourcePath, int targetSampleRate, Action<float[], int> onSamples, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            throw new ArgumentException("sourcePath is required", nameof(sourcePath));
        }

        ArgumentNullException.ThrowIfNull(onSamples);

        var ffmpeg = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            throw new InvalidOperationException("ffmpeg binary path is not available from IMediaEncoder");
        }

        var args = string.Create(
            CultureInfo.InvariantCulture,
            $"-nostdin -hide_banner -loglevel error -i \"{sourcePath}\" -vn -ac 1 -ar {targetSampleRate} -f f32le -");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to launch ffmpeg");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = process.StandardOutput.BaseStream;
        var bytes = new byte[64 * 1024];
        var samples = new float[bytes.Length / sizeof(float)];
        var carried = 0;
        long total = 0;
        int read;
        while ((read = await stdout.ReadAsync(bytes.AsMemory(carried), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var have = carried + read;
            var whole = have / sizeof(float);
            if (whole > 0)
            {
                Buffer.BlockCopy(bytes, 0, samples, 0, whole * sizeof(float));
                onSamples(samples, whole);
                total += whole;
            }

            // A read can end mid-sample: the leftover bytes start the next piece.
            carried = have - (whole * sizeof(float));
            Buffer.BlockCopy(bytes, whole * sizeof(float), bytes, 0, carried);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            _logger.LogWarning("ffmpeg exited with {Code} for {Path}: {Stderr}", process.ExitCode, sourcePath, stderr);
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}");
        }

        return total;
    }
}

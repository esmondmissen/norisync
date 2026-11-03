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
    /// Decodes the given media file to mono 32-bit float PCM at the requested sample rate.
    /// </summary>
    /// <param name="sourcePath">The absolute path of the audio file to decode.</param>
    /// <param name="targetSampleRate">Target sample rate in Hz.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decoded PCM samples as a float array.</returns>
    public async Task<float[]> DecodeAsync(string sourcePath, int targetSampleRate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            throw new ArgumentException("sourcePath is required", nameof(sourcePath));
        }

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

        using var memory = new MemoryStream();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardOutput.BaseStream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            _logger.LogWarning("ffmpeg exited with {Code} for {Path}: {Stderr}", process.ExitCode, sourcePath, stderr);
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}");
        }

        var bytes = memory.ToArray();
        var sampleCount = bytes.Length / sizeof(float);
        var samples = new float[sampleCount];
        Buffer.BlockCopy(bytes, 0, samples, 0, sampleCount * sizeof(float));
        return samples;
    }
}

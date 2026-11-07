// Извлечение пиков волновой формы через FFmpeg.
// Слой Infrastructure: декодирует моно PCM с низкой частотой и считает
// максимум амплитуды на коротких окнах. UI получает уже готовый массив.
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using MusicMixer.Application.Abstractions;
using MusicMixer.Application.Audio;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Infrastructure.Configuration;

namespace MusicMixer.Infrastructure.Analysis;

/// <summary>
/// Дешёвый по памяти экстрактор волновой формы: FFmpeg отдаёт моно f32le
/// на 8 кГц, а окна по 10 мс сворачиваются в пики без хранения всего файла.
/// </summary>
public sealed class FfmpegWaveformService : IAudioWaveformService
{
    // 8 кГц достаточно для огибающей: волновая форма показывает амплитуду,
    // а не высокочастотное содержимое. Это в разы ускоряет декодирование.
    private const int AnalysisSampleRate = 8_000;
    private const int WindowsPerSecond = 100;
    private const int MaximumBuckets = 8_192;

    private readonly FfmpegOptions _options;

    public FfmpegWaveformService(FfmpegOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        _options = options;
    }

    public async Task<IReadOnlyList<float>> ExtractPeaksAsync(
        string filePath,
        int bucketCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (bucketCount is <= 0 or > MaximumBuckets)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketCount));
        }

        if (!File.Exists(filePath))
        {
            throw new AudioProcessingException(
                $"Cannot read the waveform because the file does not exist: {filePath}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
                 {
                     "-hide_banner", "-nostdin", "-v", "error",
                     "-i", Path.GetFullPath(filePath),
                     "-map", "0:a:0",
                     "-vn", "-sn", "-dn",
                     "-ac", "1",
                     "-ar", AnalysisSampleRate.ToString(CultureInfo.InvariantCulture),
                     "-f", "f32le",
                     "-acodec", "pcm_f32le",
                     "pipe:1",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ToolUnavailableException(_options.ExecutablePath);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new ToolUnavailableException(_options.ExecutablePath, exception);
        }

        using var cancellationRegistration = cancellationToken.Register(
            static state => TryKill((Process)state!),
            process);

        var windowSamples = Math.Max(1, AnalysisSampleRate / WindowsPerSecond);
        // Чтение stdout и stderr идёт параллельно: иначе FFmpeg может
        // заблокироваться на заполненном буфере диагностики.
        var windowsTask = ReadWindowPeaksAsync(
            process.StandardOutput.BaseStream,
            windowSamples);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainAfterCancellationAsync(process, windowsTask, stderrTask)
                .ConfigureAwait(false);
            throw;
        }

        await Task.WhenAll(windowsTask, stderrTask).ConfigureAwait(false);
        var diagnostics = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new AudioProcessingException(
                $"FFmpeg could not decode the waveform for '{Path.GetFileName(filePath)}'. " +
                SummarizeDiagnostics(diagnostics));
        }

        var windows = await windowsTask.ConfigureAwait(false);
        return WaveformPeakReducer.Reduce(windows, bucketCount);
    }

    /// <summary>
    /// Читает сырые float-сэмплы и копит максимум по 10-мс окнам. Буфер
    /// ограничен одним блоком, поэтому даже многочасовой файл не занимает
    /// память целиком: остаются только тысячи оконных пиков.
    /// </summary>
    private static async Task<List<float>> ReadWindowPeaksAsync(
        Stream source,
        int windowSamples)
    {
        var windows = new List<float>(capacity: 4096);
        var buffer = new byte[64 * 1024];
        var carry = new byte[4];
        var carryCount = 0;
        var windowMaximum = 0f;
        var windowCount = 0;

        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "Waveform extraction expects a little-endian runtime.");
        }

        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                var offset = 0;
                if (carryCount > 0)
                {
                    var needed = 4 - carryCount;
                    var take = Math.Min(needed, read);
                    buffer.AsSpan(0, take).CopyTo(carry.AsSpan(carryCount));
                    carryCount += take;
                    offset += take;

                    if (carryCount == 4)
                    {
                        Accumulate(
                            BinaryPrimitives.ReadSingleLittleEndian(carry),
                            ref windowMaximum,
                            ref windowCount,
                            windowSamples,
                            windows);
                        carryCount = 0;
                    }
                }

                var sampleCount = (read - offset) / sizeof(float);
                for (var index = 0; index < sampleCount; index++)
                {
                    var value = BinaryPrimitives.ReadSingleLittleEndian(
                        buffer.AsSpan(offset + (index * sizeof(float)), sizeof(float)));
                    Accumulate(
                        value,
                        ref windowMaximum,
                        ref windowCount,
                        windowSamples,
                        windows);
                }

                offset += sampleCount * sizeof(float);
                var remainder = read - offset;
                if (remainder > 0)
                {
                    buffer.AsSpan(offset, remainder).CopyTo(carry);
                    carryCount = remainder;
                }
            }
        }
        catch (IOException)
        {
            // Cancellation closes the redirected stream; the caller rethrows
            // OperationCanceledException after the process has exited.
        }
        catch (ObjectDisposedException)
        {
        }

        if (windowCount > 0)
        {
            windows.Add(windowMaximum);
        }

        return windows;
    }

    private static void Accumulate(
        float value,
        ref float windowMaximum,
        ref int windowCount,
        int windowSamples,
        List<float> windows)
    {
        if (float.IsFinite(value))
        {
            var amplitude = MathF.Abs(value);
            if (amplitude > windowMaximum)
            {
                windowMaximum = amplitude;
            }
        }

        windowCount++;
        if (windowCount < windowSamples)
        {
            return;
        }

        windows.Add(windowMaximum);
        windowMaximum = 0;
        windowCount = 0;
    }

    private static async Task DrainAfterCancellationAsync(
        Process process,
        Task<List<float>> windowsTask,
        Task<string> stderrTask)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            await Task.WhenAll(windowsTask, stderrTask).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string SummarizeDiagnostics(string diagnostics)
    {
        var trimmed = diagnostics.Trim();
        if (trimmed.Length == 0)
        {
            return "FFmpeg returned an unknown error.";
        }

        return trimmed.Length <= 2_000 ? trimmed : trimmed[^2_000..];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}

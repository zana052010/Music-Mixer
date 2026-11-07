// Декодирование аудио в моно PCM через FFmpeg для анализа.
// Слой Infrastructure, internal: используется AudioAnalyzerService.
// FFmpeg нужен только как декодер; весь DSP выполняется управляемым кодом.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Infrastructure.Configuration;

namespace MusicMixer.Infrastructure.Analysis;

internal sealed class FfmpegPcmDecoder : IPcmDecoder
{
    private readonly FfmpegOptions _options;

    public FfmpegPcmDecoder(FfmpegOptions options)
    {
        _options = options;
    }

    public async Task<DecodedPcm> DecodeMonoAsync(
        string filePath,
        int sampleRate,
        TimeSpan maxDuration,
        CancellationToken cancellationToken)
    {
        var maxSeconds = Math.Max(1, (int)Math.Ceiling(maxDuration.TotalSeconds));
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
                     // Тихий режим и только ошибки: stdout занят сырыми PCM-байтами.
                     "-hide_banner", "-nostdin", "-v", "error",
                     "-i", Path.GetFullPath(filePath),
                     // Берём только первый аудиопоток и отбрасываем видео/субтитры.
                     "-map", "0:a:0",
                     "-vn", "-sn", "-dn",
                     // Моно и нужная частота: анализ темпа/тональности не требует стерео.
                     "-ac", "1",
                     "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
                     // Ограничение длительности защищает от многоминутного DSP.
                     "-t", maxSeconds.ToString(CultureInfo.InvariantCulture),
                     // Сырые little-endian float без заголовка — читаем прямо из pipe.
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

        using var pcmBuffer = new MemoryStream();
        // stdout и stderr читаются одновременно: иначе FFmpeg может зависнуть
        // на заполненном буфере одного из потоков.
        var stdoutTask = ReadPcmAsync(process.StandardOutput.BaseStream, pcmBuffer);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Сначала убиваем FFmpeg, затем дочитываем потоки, чтобы не
            // оставить фоновые задачи с исключениями.
            TryKill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Killing the process closes both redirected streams.
            }
            catch (ObjectDisposedException)
            {
            }

            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        var diagnostics = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new AudioAnalysisException(
                $"FFmpeg could not decode '{Path.GetFileName(filePath)}'. " +
                SummarizeDiagnostics(diagnostics));
        }

        return DecodePcmBuffer(pcmBuffer, sampleRate);
    }

    /// <summary>
    /// Проверяет, что буфер содержит целое число float-сэмплов, и отделяет
    /// слишком короткие треки: для оценки темпа нужно хотя бы несколько секунд.
    /// </summary>
    private static DecodedPcm DecodePcmBuffer(MemoryStream pcmBuffer, int sampleRate)
    {
        if (pcmBuffer.Length < sizeof(float) || pcmBuffer.Length % sizeof(float) != 0)
        {
            throw new AudioAnalysisException(
                "FFmpeg produced no usable PCM samples. The file may be empty, corrupt or unsupported.");
        }

        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("PCM analysis expects a little-endian runtime.");
        }

        // MemoryMarshal.Cast переинтерпретирует байты как float[] без копирования;
        // формат f32le гарантирует, что это корректно на little-endian платформах.
        var bytes = pcmBuffer.GetBuffer().AsSpan(0, checked((int)pcmBuffer.Length));
        var samples = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        if (samples.Length < sampleRate * 4)
        {
            throw new AudioAnalysisException("The track must be at least four seconds long.");
        }

        return new DecodedPcm(samples, sampleRate);
    }

    private static async Task ReadPcmAsync(Stream source, MemoryStream destination)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Оставляет в диагностике только последние 2000 символов: FFmpeg обычно
    /// пишет причину сбоя в конце, а весь лог в UI не нужен.
    /// </summary>
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

internal sealed record DecodedPcm(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);
}

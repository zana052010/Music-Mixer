// Infrastructure/Processing: точная до сэмпла обрезка выбранного фрагмента в PCM WAV 48 кГц.
// Вызывается из Desktop через IAudioTrimmerService до анализа и разделения стемов;
// сам запускает внешний ffmpeg через IProcessRunner. Исходный файл не перезаписывается.
using System.Globalization;
using MusicMixer.Application.Abstractions;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Domain.Models;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processes;

namespace MusicMixer.Infrastructure.Processing;

/// <summary>
/// Extracts an exact PCM WAV range from a source file. The trim happens before
/// analysis and separation, so the rest of the pipeline sees only the chosen
/// excerpt.
/// </summary>
public sealed class FfmpegAudioTrimmerService : IAudioTrimmerService
{
    private readonly IProcessRunner _processRunner;
    private readonly FfmpegOptions _ffmpegOptions;

    public FfmpegAudioTrimmerService(
        IProcessRunner processRunner,
        FfmpegOptions ffmpegOptions)
    {
        _processRunner = processRunner;
        _ffmpegOptions = ffmpegOptions;
    }

    /// <summary>
    /// Вырезает диапазон исходного файла в отдельный WAV, не изменяя источник.
    /// </summary>
    public async Task<string> TrimAsync(
        string sourceFilePath,
        string outputFilePath,
        TimeSpan start,
        TimeSpan end,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);

        if (!File.Exists(sourceFilePath))
        {
            throw new AudioProcessingException($"Source file does not exist: {sourceFilePath}");
        }

        if (start < TimeSpan.Zero || end <= start)
        {
            throw new AudioProcessingException("The audio trim range is invalid.");
        }

        var inputPath = Path.GetFullPath(sourceFilePath);
        var renderedPath = Path.GetFullPath(outputFilePath);
        // Сравниваем полные пути без учёта регистра: результат обрезки не должен затереть
        // файл, который пользователь выбрал как источник.
        if (string.Equals(inputPath, renderedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new AudioProcessingException("A trim cannot overwrite its source file.");
        }

        // Конвейер ждёт WAV: в нём границы сэмплов определены однозначно,
        // в отличие от сжатых контейнеров с нестабильной длительностью.
        if (!string.Equals(Path.GetExtension(renderedPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new AudioProcessingException("Trimmed previews must be written as WAV.");
        }

        var outputDirectory = Path.GetDirectoryName(renderedPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var duration = end - start;
        // -ss стоит после -i: ffmpeg декодирует и отбрасывает начало, поэтому границы точны,
        // а не привязаны к ключевым кадрам входного формата.
        //
        // На выходе только первая аудиодорожка в фиксированном формате 48 кГц/стерео/PCM:
        // это общий знаменатель для анализа, Demucs и BASS.
        // -progress pipe:1 даёт машиночитаемые строки, -nostats убирает дублирующую статистику.
        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i", inputPath,
            "-ss", start.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture),
            "-t", duration.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture),
            "-map", "0:a:0",
            "-vn",
            "-sn",
            "-dn",
            "-ar", "48000",
            "-ac", "2",
            "-c:a", "pcm_s16le",
            "-progress", "pipe:1",
            "-nostats",
            renderedPath,
        };

        // successful остаётся false до конца: при сбое или отмене недописанный WAV будет удалён.
        var progressState = new TrimProgressState(progress, duration);
        var successful = false;
        progress?.Report(new OperationProgress(0.01, "Extracting audio selection"));

        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessLaunchRequest(_ffmpegOptions.ExecutablePath, arguments),
                progressState.Observe,
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new AudioProcessingException(
                    $"FFmpeg could not trim the source audio (exit code {result.ExitCode}).",
                    result.OutputTail);
            }

            // Код возврата 0 ещё не гарантирует файл: проверяем, что ffmpeg действительно его создал.
            var output = new FileInfo(renderedPath);
            if (!output.Exists || output.Length == 0)
            {
                throw new AudioProcessingException(
                    "FFmpeg reported success but produced no trimmed audio file.",
                    result.OutputTail);
            }

            progress?.Report(OperationProgress.Complete("Trim complete"));
            successful = true;
            return renderedPath;
        }
        finally
        {
            if (!successful)
            {
                TryDelete(renderedPath);
            }
        }
    }

    // Уборка не должна маскировать исходную ошибку, поэтому сбои удаления здесь игнорируются.
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Переводит out_time_us из ffmpeg в долю ожидаемой длительности фрагмента.
    private sealed class TrimProgressState
    {
        private readonly IProgress<OperationProgress>? _progress;
        private readonly double _expectedSeconds;
        private readonly object _gate = new();
        private int _lastPercent = -1;

        public TrimProgressState(
            IProgress<OperationProgress>? progress,
            TimeSpan expectedDuration)
        {
            _progress = progress;
            _expectedSeconds = expectedDuration.TotalSeconds;
        }

        public void Observe(ProcessOutputLine output)
        {
            if (_progress is null || _expectedSeconds <= 0)
            {
                return;
            }

            var seconds = ParseRenderedSeconds(output.Text);
            if (seconds is null)
            {
                return;
            }

            // Потолок 0.995 и только рост: 100% отдаёт финальный Complete,
            // а повторные строки ffmpeg не должны откатывать прогресс назад.
            var fraction = Math.Clamp(seconds.Value / _expectedSeconds, 0, 0.995);
            var percent = (int)Math.Floor(fraction * 100);
            lock (_gate)
            {
                if (percent <= _lastPercent)
                {
                    return;
                }

                _lastPercent = percent;
                _progress.Report(new OperationProgress(
                    Math.Max(0.01, fraction),
                    $"Extracting audio selection ({percent}%)"));
            }
        }

        private static double? ParseRenderedSeconds(string line)
        {
            // В ffmpeg out_time_ms исторически тоже микросекунды, поэтому оба поля делим на миллион.
            if (TryReadLongValue(line, "out_time_us=", out var microseconds))
            {
                return microseconds / 1_000_000.0;
            }

            if (TryReadLongValue(line, "out_time_ms=", out var milliseconds))
            {
                return milliseconds / 1_000_000.0;
            }

            return null;
        }

        private static bool TryReadLongValue(string line, string prefix, out long value)
        {
            value = 0;
            return line.StartsWith(prefix, StringComparison.Ordinal) &&
                   long.TryParse(
                       line.AsSpan(prefix.Length).Trim(),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out value);
        }
    }
}

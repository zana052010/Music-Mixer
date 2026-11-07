// Улучшение извлечённых стемов через FFmpeg.
// Слой Infrastructure; реализует IAudioStemEnhancerService.
// Вызывается из MashupWorkflow сразу после разделения на инструментал и вокал.
//
// Цепочки намеренно мягкие: задача — убрать гул и муть, добавить разборчивость,
// а не пережать трек. Финальный микс по-прежнему сводится отдельным сервисом.
using System.Globalization;
using MusicMixer.Application.Abstractions;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Domain.Models;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processes;

namespace MusicMixer.Infrastructure.Processing;

/// <summary>
/// FFmpeg-адаптер постобработки стемов. Инструментал и вокал получают разные
/// EQ-профили: вокалу нужен больший подъём разборчивости и срез низа, музыке —
/// только лёгкая чистка низа и середины.
/// </summary>
public sealed class FfmpegStemEnhancerService : IAudioStemEnhancerService
{
    private readonly IProcessRunner _processRunner;
    private readonly FfmpegOptions _options;

    public FfmpegStemEnhancerService(IProcessRunner processRunner, FfmpegOptions options)
    {
        _processRunner = processRunner;
        _options = options;
    }

    public async Task<string> EnhanceAsync(
        string sourceFilePath,
        string outputFilePath,
        AudioStemKind kind,
        TimeSpan sourceDuration,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceDuration, TimeSpan.Zero);

        var inputPath = Path.GetFullPath(sourceFilePath);
        var renderedPath = Path.GetFullPath(outputFilePath);
        if (!File.Exists(inputPath))
        {
            throw new AudioProcessingException($"Stem to enhance was not found: {inputPath}");
        }

        if (string.Equals(inputPath, renderedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new AudioProcessingException("Stem enhancement must not overwrite its source.");
        }

        if (!string.Equals(Path.GetExtension(renderedPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new AudioProcessingException("Enhanced stems must be written as WAV.");
        }

        var outputDirectory = Path.GetDirectoryName(renderedPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", inputPath,
            "-map", "0:a:0",
            "-vn", "-sn", "-dn",
            "-af", BuildFilter(kind),
            "-ar", "48000",
            "-ac", "2",
            "-c:a", "pcm_s16le",
            "-progress", "pipe:1", "-nostats",
            renderedPath,
        };

        var progressState = new EnhancementProgressState(progress, sourceDuration);
        var successful = false;
        progress?.Report(new OperationProgress(0.01, "Enhancing extracted stem"));

        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessLaunchRequest(_options.ExecutablePath, arguments),
                progressState.Observe,
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new AudioProcessingException(
                    $"FFmpeg could not enhance the {Describe(kind)} stem " +
                    $"(exit code {result.ExitCode}).",
                    result.OutputTail);
            }

            var output = new FileInfo(renderedPath);
            if (!output.Exists || output.Length == 0)
            {
                throw new AudioProcessingException(
                    "FFmpeg reported success but produced no enhanced stem.",
                    result.OutputTail);
            }

            progress?.Report(OperationProgress.Complete("Stem enhancement complete"));
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

    /// <summary>
    /// Возвращает filter-цепочку для конкретного стема. Значения подобраны
    /// так, чтобы обработка была слышна, но не превращала звук в «пережатый».
    /// </summary>
    private static string BuildFilter(AudioStemKind kind) => kind switch
    {
        AudioStemKind.Vocal => string.Concat(
            // Гул и инфраниз у вокала не несут полезной информации.
            "highpass=f=70,",
            // Убираем «коробку» в низкой середине.
            "equalizer=f=300:t=q:w=1:g=-2,",
            // Возвращаем разборчивость согласных.
            "equalizer=f=3200:t=q:w=1.2:g=2.5,",
            // Немного воздуха сверху.
            "treble=g=1:f=9000,",
            // Очень мягкая компрессия, чтобы тихие слова не терялись.
            "acompressor=threshold=0.12:ratio=2:attack=10:release=150:makeup=1.3,",
            "alimiter=limit=0.95"),
        AudioStemKind.Instrumental => string.Concat(
            "highpass=f=30,",
            "equalizer=f=250:t=q:w=1:g=-1.5,",
            "equalizer=f=3000:t=q:w=1.2:g=1.5,",
            "treble=g=0.8:f=9000,",
            "acompressor=threshold=0.15:ratio=1.8:attack=15:release=180:makeup=1.15,",
            "alimiter=limit=0.95"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown stem kind."),
    };

    private static string Describe(AudioStemKind kind) => kind switch
    {
        AudioStemKind.Vocal => "vocal",
        AudioStemKind.Instrumental => "instrumental",
        _ => "audio",
    };

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

    /// <summary>
    /// Переводит out_time_us FFmpeg в долю длительности исходного стема.
    /// </summary>
    private sealed class EnhancementProgressState
    {
        private readonly IProgress<OperationProgress>? _progress;
        private readonly double _expectedSeconds;
        private readonly object _gate = new();
        private int _lastPercent = -1;

        public EnhancementProgressState(
            IProgress<OperationProgress>? progress,
            TimeSpan sourceDuration)
        {
            _progress = progress;
            _expectedSeconds = sourceDuration.TotalSeconds;
        }

        public void Observe(ProcessOutputLine output)
        {
            if (_progress is null || _expectedSeconds <= 0 || output.IsError)
            {
                return;
            }

            var seconds = ParseRenderedSeconds(output.Text);
            if (seconds is null)
            {
                return;
            }

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
                    $"Enhancing extracted stem ({percent}%)"));
            }
        }

        private static double? ParseRenderedSeconds(string line)
        {
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

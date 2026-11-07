// Сведение готового мэшапа в один WAV через FFmpeg.
// Слой Infrastructure; реализует IMixdownService.
// Вызывается из MainViewModel по кнопке «Скачать микс».
//
// Схема фильтра:
//   инструментал -> volume -> [inst]
//   вокал -> volume -> adelay (или atrim) -> [voc]
//   [inst][voc] -> amix -> [mix]
//
// normalize=0 отключает автоматическое деление громкости на число входов,
// поэтому громкости из UI соответствуют результату экспорта.
using System.Globalization;
using MusicMixer.Application.Abstractions;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Domain.Models;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processes;

namespace MusicMixer.Infrastructure.Processing;

/// <summary>
/// Сводит два стема в один PCM WAV 48 кГц/стерео.
/// Вокал уже содержит изменения темпа и высоты тона; здесь добавляются только
/// стартовое смещение и громкости, которые пользователь слышит в предпросмотре.
/// </summary>
public sealed class FfmpegMixdownService : IMixdownService
{
    private readonly IProcessRunner _processRunner;
    private readonly FfmpegOptions _options;

    public FfmpegMixdownService(IProcessRunner processRunner, FfmpegOptions options)
    {
        _processRunner = processRunner;
        _options = options;
    }

    public async Task<string> ExportAsync(
        string instrumentalFilePath,
        string vocalFilePath,
        TimeSpan vocalStartOffset,
        double instrumentalVolume,
        double vocalVolume,
        TimeSpan expectedDuration,
        string outputFilePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentalFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vocalFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedDuration, TimeSpan.Zero);

        if (!double.IsFinite(instrumentalVolume) || !double.IsFinite(vocalVolume))
        {
            throw new ArgumentOutOfRangeException(nameof(instrumentalVolume));
        }

        if (vocalStartOffset < TimeSpan.FromMinutes(-30) ||
            vocalStartOffset > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(vocalStartOffset));
        }

        var instrumentalPath = Path.GetFullPath(instrumentalFilePath);
        var vocalPath = Path.GetFullPath(vocalFilePath);
        EnsureFileExists(instrumentalPath, "instrumental");
        EnsureFileExists(vocalPath, "vocal");

        var finalPath = Path.GetFullPath(outputFilePath);
        if (!string.Equals(Path.GetExtension(finalPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Mixdown must produce a .wav file.", nameof(outputFilePath));
        }

        var outputDirectory = Path.GetDirectoryName(finalPath)
            ?? throw new ArgumentException("The output file needs a parent directory.", nameof(outputFilePath));
        Directory.CreateDirectory(outputDirectory);

        // Пишем во временный файл рядом с целевым и публикуем его только после
        // успешного завершения FFmpeg. Так неудачный экспорт не портит
        // существующий файл пользователя.
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".musicmixer-mix-{Guid.NewGuid():N}.wav");

        var filter = BuildFilter(vocalStartOffset, instrumentalVolume, vocalVolume);
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", instrumentalPath,
            "-i", vocalPath,
            "-filter_complex", filter,
            "-map", "[mix]",
            "-ar", _options.OutputSampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", _options.OutputChannels.ToString(CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le",
            "-progress", "pipe:1", "-nostats",
            temporaryPath,
        };

        var reporter = new FfmpegProgressReporter(progress, expectedDuration);

        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessLaunchRequest(_options.ExecutablePath, arguments),
                output => reporter.Observe(output),
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new AudioProcessingException(
                    $"FFmpeg could not export the mix (exit code {result.ExitCode}).",
                    result.OutputTail);
            }

            var output = new FileInfo(temporaryPath);
            if (!output.Exists || output.Length == 0)
            {
                throw new AudioProcessingException(
                    "FFmpeg finished without producing a usable mixdown file.");
            }

            File.Move(temporaryPath, finalPath, overwrite: true);
            progress?.Report(OperationProgress.Complete("Mix exported"));
            return finalPath;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    /// <summary>
    /// Собирает filter_complex. Положительное смещение задерживает вокал,
    /// отрицательное — срезает его начало, чтобы вокал вступил раньше.
    /// </summary>
    private static string BuildFilter(
        TimeSpan vocalStartOffset,
        double instrumentalVolume,
        double vocalVolume)
    {
        // Громкости ограничиваем 0..1: UI уже это делает, но сервис не должен
        // доверять вызывающему коду и допускать клиппинг из-за значения > 1.
        var instrumentalGain = Format(Math.Clamp(instrumentalVolume, 0, 1));
        var vocalGain = Format(Math.Clamp(vocalVolume, 0, 1));
        var offsetMilliseconds = (long)Math.Round(
            vocalStartOffset.TotalMilliseconds,
            MidpointRounding.AwayFromZero);

        var vocalChain = $"volume={vocalGain}";
        if (offsetMilliseconds > 0)
        {
            // all=1 применяет задержку ко всем каналам, включая моно-вокал.
            vocalChain += $",adelay=delays={offsetMilliseconds}:all=1";
        }
        else if (offsetMilliseconds < 0)
        {
            vocalChain += $",atrim=start={Format(Math.Abs(vocalStartOffset.TotalSeconds))},asetpts=PTS-STARTPTS";
        }

        return string.Concat(
            "[0:a]volume=", instrumentalGain, "[inst];",
            "[1:a]", vocalChain, "[voc];",
            // duration=first: инструментал — ведущие часы, как и в плеере.
            // Если вокал длиннее, экспорт не удлиняет трек за его конец.
            // Лимитер после amix не даёт сумме двух стемов уйти в клиппинг.
            "[inst][voc]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,",
            "alimiter=limit=0.95[mix]");
    }

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void EnsureFileExists(string path, string role)
    {
        if (!File.Exists(path))
        {
            throw new AudioProcessingException($"{role} stem was not found: {path}");
        }
    }

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
    /// Разбирает машиночитаемый вывод FFmpeg (-progress pipe:1).
    /// out_time_us приходит в микросекундах; делим на длительность, чтобы
    /// получить долю 0..1 для общего индикатора.
    /// </summary>
    private sealed class FfmpegProgressReporter
    {
        private const string Prefix = "out_time_us=";
        private readonly IProgress<OperationProgress>? _progress;
        private readonly double _durationSeconds;

        public FfmpegProgressReporter(
            IProgress<OperationProgress>? progress,
            TimeSpan expectedDuration)
        {
            _progress = progress;
            _durationSeconds = expectedDuration.TotalSeconds;
        }

        public void Observe(ProcessOutputLine output)
        {
            // Прогресс пишется в stdout; stderr содержит обычный лог FFmpeg.
            if (output.IsError || _durationSeconds <= 0)
            {
                return;
            }

            var line = output.Text.AsSpan().Trim();
            if (!line.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return;
            }

            var value = line[Prefix.Length..];
            if (!long.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var microseconds))
            {
                return;
            }

            var fraction = Math.Clamp(
                microseconds / 1_000_000.0 / _durationSeconds,
                0,
                0.99);
            _progress?.Report(new OperationProgress(fraction, "Mixing audio"));
        }
    }
}

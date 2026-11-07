// Адаптер Demucs: разделение трека на вокал и инструментал через CLI.
// Слой Infrastructure; реализует IAudioSeparatorService.
// Вызывается из MashupWorkflow для дорожек A и B.
using System.Globalization;
using System.Text.RegularExpressions;
using MusicMixer.Application.Abstractions;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Domain.Models;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processes;

namespace MusicMixer.Infrastructure.Separation;

/// <summary>
/// Запускает Demucs в режиме двух стемов и превращает проценты tqdm в
/// нормализованный прогресс. Demucs пишет прогресс-бар в stderr,
/// поэтому читаются оба потока вывода.
/// </summary>
public sealed class DemucsCliSeparatorService : IAudioSeparatorService
{
    private static readonly Regex PercentagePattern = new(
        @"(?<percent>\d{1,3})%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IProcessRunner _processRunner;
    private readonly DemucsOptions _options;

    public DemucsCliSeparatorService(IProcessRunner processRunner, DemucsOptions options)
    {
        _processRunner = processRunner;
        _options = options;
    }

    public async Task<StemSeparationResult> SeparateAsync(
        AudioTrack source,
        string outputDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (!File.Exists(source.FilePath))
        {
            throw new AudioProcessingException($"Source file does not exist: {source.FilePath}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputDirectory);

        // У каждого запуска свой подкаталог: повторная попытка не смешает
        // старые стемы с новыми и не перезапишет удачный результат.
        var runDirectory = Path.Combine(
            Path.GetFullPath(outputDirectory),
            $"demucs-{CreateSlug(source.Metadata.Title)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);

        var arguments = BuildArguments(source.FilePath, runDirectory);
        var progressState = new DemucsProgressState(progress);

        progress?.Report(new OperationProgress(0.01, $"Loading Demucs model {_options.ModelName}"));
        var successful = false;

        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessLaunchRequest(_options.ExecutablePath, arguments, runDirectory),
                progressState.Observe,
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new AudioProcessingException(
                    $"Demucs failed with exit code {result.ExitCode}. " +
                    "Verify that the input is decodable and that the Demucs environment is healthy.",
                    result.OutputTail);
            }

            progress?.Report(new OperationProgress(0.98, "Collecting separated stems"));
            var instrumentalPath = FindStem(runDirectory, "no_vocals");
            var vocalsPath = FindStem(runDirectory, "vocals");
            EnsureNonEmpty(instrumentalPath, "instrumental");
            EnsureNonEmpty(vocalsPath, "vocal");

            progress?.Report(OperationProgress.Complete("Stems separated"));
            successful = true;

            return new StemSeparationResult(source.FilePath, instrumentalPath, vocalsPath);
        }
        finally
        {
            // Частичный вывод удаляем только при сбое: при успехе файлы нужны
            // сессии до конца работы приложения.
            if (!successful)
            {
                TryDeleteDirectory(runDirectory);
            }
        }
    }

    /// <summary>
    /// Собирает аргументы CLI. --two-stems vocals означает, что Demucs
    /// создаст ровно два файла: vocals.wav и no_vocals.wav.
    /// </summary>
    private IReadOnlyList<string> BuildArguments(string sourcePath, string runDirectory)
    {
        if (string.IsNullOrWhiteSpace(_options.ModelName))
        {
            throw new AudioProcessingException("Demucs model name is not configured.");
        }

        var arguments = new List<string>
        {
            "--two-stems", "vocals",
            "--name", _options.ModelName,
            "--out", runDirectory,
        };

        if (!string.IsNullOrWhiteSpace(_options.Device))
        {
            arguments.Add("--device");
            arguments.Add(_options.Device);
        }

        if (_options.Jobs > 0)
        {
            arguments.Add("--jobs");
            arguments.Add(_options.Jobs.ToString(CultureInfo.InvariantCulture));
        }

        arguments.AddRange(_options.ExtraArguments);
        arguments.Add(Path.GetFullPath(sourcePath));
        return arguments;
    }

    /// <summary>
    /// Ищет ровно один файл с нужным именем. Неоднозначный результат лучше
    /// отклонить, чем угадывать, какой из стемов правильный.
    /// </summary>
    private static string FindStem(string runDirectory, string stemName)
    {
        var candidates = Directory.EnumerateFiles(runDirectory, "*", SearchOption.AllDirectories)
            .Where(path =>
                string.Equals(Path.GetFileNameWithoutExtension(path), stemName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return candidates.Length switch
        {
            1 => Path.GetFullPath(candidates[0]),
            0 => throw new AudioProcessingException(
                $"Demucs completed but did not produce a '{stemName}.wav' stem."),
            _ => throw new AudioProcessingException(
                $"Demucs produced multiple '{stemName}.wav' files; refusing to guess which one to use."),
        };
    }

    private static void EnsureNonEmpty(string path, string label)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            throw new AudioProcessingException($"Demucs produced an empty {label} stem.");
        }
    }

    private static string CreateSlug(string title)
    {
        var characters = title
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .Take(40)
            .ToArray();
        var slug = new string(characters).Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "track" : slug;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
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
    /// Разбирает строки Demucs/tqdm. Прогресс должен только расти, поэтому
    /// повторные и меньшие проценты игнорируются; фазы скачивания модели и
    /// разделения отображаются в разные участки общего прогресса.
    /// </summary>
    private sealed class DemucsProgressState
    {
        private readonly IProgress<OperationProgress>? _progress;
        private readonly object _gate = new();
        private Stage _stage = Stage.Unknown;
        private int _lastPercent = -1;
        private double _lastReported;

        public DemucsProgressState(IProgress<OperationProgress>? progress)
        {
            _progress = progress;
        }

        public void Observe(ProcessOutputLine output)
        {
            var line = output.Text;
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (_gate)
            {
                if (line.Contains("download", StringComparison.OrdinalIgnoreCase))
                {
                    _stage = Stage.Download;
                }
                else if (line.Contains("separat", StringComparison.OrdinalIgnoreCase))
                {
                    _stage = Stage.Separation;
                }

                var matches = PercentagePattern.Matches(line);
                if (matches.Count == 0)
                {
                    return;
                }

                var rawPercent = matches[^1].Groups["percent"].Value;
                if (!int.TryParse(rawPercent, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) ||
                    percent is < 0 or > 100)
                {
                    return;
                }

                if (percent == _lastPercent)
                {
                    return;
                }

                _lastPercent = percent;
                var fraction = MapToOverall(percent);
                if (fraction <= _lastReported)
                {
                    return;
                }

                _lastReported = fraction;
                var message = _stage switch
                {
                    Stage.Download => $"Downloading model ({percent}%)",
                    Stage.Separation => $"Separating stems ({percent}%)",
                    _ => $"Running Demucs ({percent}%)",
                };

                _progress?.Report(new OperationProgress(fraction, message));
            }
        }

        private double MapToOverall(int percent)
        {
            // Первые 1–9 % отданы загрузке модели, остальные 10–97 % —
            // непосредственно разделению. Так индикатор не стоит на месте,
            // пока Demucs скачивает htdemucs при первом запуске.
            var normalized = percent / 100.0;
            return _stage switch
            {
                Stage.Download => 0.01 + (0.08 * normalized),
                Stage.Separation => 0.10 + (0.87 * normalized),
                _ => 0.01 + (0.96 * normalized),
            };
        }

        private enum Stage
        {
            Unknown,
            Download,
            Separation,
        }
    }
}

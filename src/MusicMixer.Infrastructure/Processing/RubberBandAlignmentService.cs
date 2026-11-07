// Infrastructure/Processing: выравнивание вокала под инструментал фильтром rubberband в ffmpeg.
// Вызывается из MashupWorkflow через IAudioProcessingService; сам запускает ffmpeg через IProcessRunner.
// Один проход меняет темп и высоту тона, сохраняя форманты и стереопанораму.
using System.Globalization;
using MusicMixer.Application.Abstractions;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Domain.Models;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processes;

namespace MusicMixer.Infrastructure.Processing;

/// <summary>
/// FFmpeg/librubberband adapter. Both time stretching and pitch shifting are
/// performed in a single filter pass so the vocal is resampled only once.
/// </summary>
public sealed class RubberBandAlignmentService : IAudioProcessingService
{
    private readonly IProcessRunner _processRunner;
    private readonly FfmpegOptions _options;

    public RubberBandAlignmentService(IProcessRunner processRunner, FfmpegOptions options)
    {
        _processRunner = processRunner;
        _options = options;
    }

    /// <summary>
    /// Рендерит вокальный стем с новым темпом и высотой тона в отдельный WAV.
    /// </summary>
    public async Task<string> RenderAlignedVocalsAsync(
        string vocalFilePath,
        string outputFilePath,
        AlignmentParameters parameters,
        TimeSpan sourceDuration,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vocalFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);
        ArgumentNullException.ThrowIfNull(parameters);

        if (!File.Exists(vocalFilePath))
        {
            throw new AudioProcessingException($"Vocal stem does not exist: {vocalFilePath}");
        }

        if (sourceDuration <= TimeSpan.Zero)
        {
            throw new AudioProcessingException("Vocal duration must be known before alignment.");
        }

        var inputPath = Path.GetFullPath(vocalFilePath);
        var renderedPath = Path.GetFullPath(outputFilePath);
        if (string.Equals(inputPath, renderedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new AudioProcessingException("The aligned output must not overwrite the source vocal stem.");
        }

        if (!string.Equals(Path.GetExtension(renderedPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new AudioProcessingException("The preview renderer requires a .wav output path.");
        }

        var outputDirectory = Path.GetDirectoryName(renderedPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        // При ratio больше единицы вокал ускоряется и становится короче исходника;
        // именно эта длина нужна, чтобы перевести out_time ffmpeg в проценты.
        var expectedDurationSeconds = sourceDuration.TotalSeconds / parameters.PlaybackSpeedRatio;
        var progressState = new FfmpegProgressState(progress, expectedDurationSeconds);
        var arguments = BuildArguments(inputPath, renderedPath, parameters);
        var successful = false;

        progress?.Report(new OperationProgress(0.01, "Starting FFmpeg alignment"));
        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessLaunchRequest(_options.ExecutablePath, arguments),
                progressState.Observe,
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                var diagnostics = result.OutputTail;
                var missingFilter = diagnostics.Contains(
                    "No such filter: 'rubberband'",
                    StringComparison.OrdinalIgnoreCase) ||
                    diagnostics.Contains(
                        "Unknown filter 'rubberband'",
                        StringComparison.OrdinalIgnoreCase);

                var message = missingFilter
                    ? "This FFmpeg build does not include the librubberband filter. " +
                      "Install an FFmpeg build compiled with --enable-librubberband."
                    : $"FFmpeg alignment failed with exit code {result.ExitCode}.";

                throw new AudioProcessingException(message, diagnostics);
            }

            var output = new FileInfo(renderedPath);
            if (!output.Exists || output.Length == 0)
            {
                throw new AudioProcessingException(
                    "FFmpeg reported success but produced no aligned vocal file.",
                    result.OutputTail);
            }

            progress?.Report(OperationProgress.Complete("Vocal alignment rendered"));
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

    private IReadOnlyList<string> BuildArguments(
        string inputPath,
        string outputPath,
        AlignmentParameters parameters)
    {
        // InvariantCulture обязателен: ffmpeg понимает только точку как десятичный разделитель.
        var tempo = parameters.PlaybackSpeedRatio.ToString("0.######", CultureInfo.InvariantCulture);
        var pitch = parameters.PitchScaleRatio.ToString("0.######", CultureInfo.InvariantCulture);
        // Один фильтр делает и time-stretch, и pitch-shift: вокал ресемплится один раз,
        // поэтому артефактов и щелчков меньше, чем при двух последовательных проходах.
        // formant=preserved сохраняет тембр голоса, transients=crisp — атаки согласных,
        // channels=together обрабатывает каналы согласованно и не разваливает стереопанораму.
        var filter =
            $"rubberband=tempo={tempo}:pitch={pitch}:transients=crisp:formant=preserved:pitchq=quality:channels=together";

        return
        [
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i", inputPath,
            "-map", "0:a:0",
            "-vn",
            "-sn",
            "-dn",
            "-af", filter,
            "-ar", _options.OutputSampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", _options.OutputChannels.ToString(CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le",
            "-progress", "pipe:1",
            "-nostats",
            outputPath,
        ];
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

    private sealed class FfmpegProgressState
    {
        private readonly IProgress<OperationProgress>? _progress;
        private readonly double _expectedDurationSeconds;
        private readonly object _gate = new();
        private int _lastPercent = -1;

        public FfmpegProgressState(IProgress<OperationProgress>? progress, double expectedDurationSeconds)
        {
            _progress = progress;
            _expectedDurationSeconds = expectedDurationSeconds;
        }

        public void Observe(ProcessOutputLine output)
        {
            if (_progress is null || _expectedDurationSeconds <= 0)
            {
                return;
            }

            var time = ParseRenderedSeconds(output.Text);
            if (time is null)
            {
                return;
            }

            var fraction = Math.Clamp(time.Value / _expectedDurationSeconds, 0, 0.995);
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
                    $"Time-stretching and pitch-shifting ({percent}%)"));
            }
        }

        private static double? ParseRenderedSeconds(string line)
        {
            // FFmpeg's -progress pipe output uses microseconds for both
            // out_time_us and the misleadingly named out_time_ms.
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
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            return long.TryParse(
                line.AsSpan(prefix.Length).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }
    }
}

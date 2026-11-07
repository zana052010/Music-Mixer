// Инфраструктурный слой: анализ аудио — BPM, фаза даунбита и тональность.
// Оркеструет декодирование в моно PCM и управляемый DSP в Task.Run.
// Вызывается через IAudioAnalyzerService из MashupWorkflow при подготовке мэшапа.

using MusicMixer.Application.Abstractions;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Domain.Models;
using MusicMixer.Infrastructure.Analysis.Dsp;
using MusicMixer.Infrastructure.Configuration;

namespace MusicMixer.Infrastructure.Analysis;

/// <summary>
/// Self-contained analyzer: FFmpeg is used only to decode a mono PCM stream.
/// Tempo, downbeat and key detection run in managed code on a worker thread,
/// so the UI thread never performs DSP work.
/// </summary>
public sealed class AudioAnalyzerService : IAudioAnalyzerService
{
    private readonly AudioAnalysisOptions _analysisOptions;
    private readonly IPcmDecoder _decoder;

    /// <summary>
    /// Создаёт анализатор для десктопного сценария, где аудио декодирует FFmpeg в моно PCM.
    /// </summary>
    public AudioAnalyzerService(FfmpegOptions ffmpegOptions, AudioAnalysisOptions analysisOptions)
    {
        ArgumentNullException.ThrowIfNull(ffmpegOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegOptions.ExecutablePath);
        ArgumentNullException.ThrowIfNull(analysisOptions);

        _analysisOptions = analysisOptions;
        ValidateOptions();
        _decoder = new FfmpegPcmDecoder(ffmpegOptions);
    }

    /// <summary>
    /// Декодирует первые минуты трека в моно PCM и оценивает темп, фазу даунбита и тональность.
    /// Результат содержит длительность уже декодированного фрагмента, а не исходного файла.
    /// </summary>
    public async Task<AudioAnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new AudioAnalysisException($"Audio file does not exist: {filePath}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Проценты — грубые вехи этапов: FFmpeg не сообщает прогресс декодирования,
        // поэтому шкала отражает ход анализа, а не реальное оставшееся время.
        progress?.Report(new OperationProgress(0.02, "Decoding audio for analysis"));

        DecodedPcm pcm;
        try
        {
            // Декодируем сразу в моно f32le: DSP работает с одним каналом, а частоту
            // дискретизации приводит декодер, чтобы все детекторы получили один формат.
            pcm = await _decoder.DecodeMonoAsync(
                filePath,
                _analysisOptions.SampleRate,
                _analysisOptions.MaxDuration,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MusicMixerException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AudioAnalysisException("Audio decoding failed.", exception);
        }

        progress?.Report(new OperationProgress(0.30, "Detecting tempo and downbeat"));
        TempoEstimate tempo;
        try
        {
            // Управляемый DSP синхронный и грузит CPU, поэтому уводим его в Task.Run:
            // UI-поток остаётся отзывчивым, а токен отмены проверяется внутри детектора.
            tempo = await Task.Run(
                () => TempoDetector.Analyze(pcm.Samples, pcm.SampleRate, _analysisOptions, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MusicMixerException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AudioAnalysisException("Tempo analysis failed.", exception);
        }

        progress?.Report(new OperationProgress(0.62, "Detecting musical key"));
        KeyEstimate key;
        try
        {
            // Тональность считаем отдельным проходом по тем же сэмплам: у этапа своя
            // веха прогресса и своя обработка ошибок, а кэш спектра не нужен.
            key = await Task.Run(
                () => KeyDetector.Detect(pcm.Samples, pcm.SampleRate, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MusicMixerException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AudioAnalysisException("Key analysis failed.", exception);
        }

        progress?.Report(new OperationProgress(0.95, "Finalizing analysis"));
        // Длительность берём из декодированного PCM: анализ ограничен MaxDuration
        // и для длинного трека вернёт только проанализированный фрагмент.
        var result = new AudioAnalysisResult(
            pcm.Duration,
            tempo.BeatsPerMinute,
            tempo.DownbeatOffset,
            key.Key,
            tempo.Confidence,
            key.Confidence);

        progress?.Report(OperationProgress.Complete("Analysis complete"));
        return result;
    }

    private void ValidateOptions()
    {
        // Верхняя граница 48 кГц отсекает бессмысленные для анализа частоты и
        // ограничивает память, которую займёт декодированный массив сэмплов.
        if (_analysisOptions.SampleRate is < 8_000 or > 48_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(_analysisOptions.SampleRate),
                "Analysis sample rate must be between 8000 and 48000 Hz.");
        }

        if (_analysisOptions.MinimumBpm is < 20 or > 240 ||
            _analysisOptions.MaximumBpm is < 20 or > 400 ||
            _analysisOptions.MinimumBpm >= _analysisOptions.MaximumBpm)
        {
            throw new ArgumentOutOfRangeException(nameof(_analysisOptions.MinimumBpm), "Invalid BPM range.");
        }

        if (_analysisOptions.MaxDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(_analysisOptions.MaxDuration));
        }
    }
}

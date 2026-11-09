// Главный сценарий приложения: из двух исходных треков собрать готовый мэшап.
// Слой Application: оркестрирует порты анализа, разделения и рендеринга,
// ничего не зная о Demucs, FFmpeg, BASS и Avalonia.
// Вызывается из MainViewModel; сам вызывает сервисы Infrastructure через интерфейсы.
using MusicMixer.Application.Abstractions;
using MusicMixer.Domain.Alignment;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Domain.Models;

namespace MusicMixer.Application.Mashup;

/// <summary>
/// Оркестратор конвейера: анализ обоих треков, разделение на стемы,
/// улучшение инструментала и вокала, расчёт выравнивания и рендеринг вокала.
/// Единственное место, где зафиксирован порядок этапов и распределение
/// общего прогресса.
/// </summary>
public sealed class MashupWorkflow : IMashupWorkflow
{
    private readonly IAudioAnalyzerService _analyzer;
    private readonly IAudioSeparatorService _separator;
    private readonly IAudioProcessingService _processing;
    private readonly IAudioStemEnhancerService _stemEnhancer;

    public MashupWorkflow(
        IAudioAnalyzerService analyzer,
        IAudioSeparatorService separator,
        IAudioProcessingService processing,
        IAudioStemEnhancerService stemEnhancer)
    {
        _analyzer = analyzer;
        _separator = separator;
        _processing = processing;
        _stemEnhancer = stemEnhancer;
    }

    public async Task<MashupPreparationResult> PrepareAsync(
        AudioTrack trackA,
        AudioTrack trackB,
        string workspaceDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool enhanceStems = true)
    {
        ArgumentNullException.ThrowIfNull(trackA);
        ArgumentNullException.ThrowIfNull(trackB);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);

        // Проверяем входы до запуска тяжёлых инструментов: отсутствующий файл
        // должен дать понятную ошибку сразу, а не после загрузки модели Demucs.
        EnsureReadable(trackA.FilePath, "Track A");
        EnsureReadable(trackB.FilePath, "Track B");
        Directory.CreateDirectory(workspaceDirectory);

        // Этап 1-2: анализ. Каждый дочерний сервис сообщает прогресс в 0..1,
        // а ProgressScope растягивает его на свою долю общего прогресса.
        progress?.Report(new OperationProgress(0, "Analyzing Track A"));
        var analyzedA = await AnalyzeAsync(
            trackA,
            new ProgressScope(progress, 0.00, 0.05, "Track A analysis"),
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new OperationProgress(0.05, "Analyzing Track B"));
        var analyzedB = await AnalyzeAsync(
            trackB,
            new ProgressScope(progress, 0.05, 0.10, "Track B analysis"),
            cancellationToken).ConfigureAwait(false);

        // Этап 3-4: разделение на стемы. Дорожка A даёт инструментал,
        // дорожка B — вокал. Demucs создаёт оба файла за один запуск.
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new OperationProgress(0.10, "Separating Track A instrumental"));
        var stemsA = await _separator.SeparateAsync(
            analyzedA,
            Path.Combine(workspaceDirectory, "track-a"),
            new ProgressScope(progress, 0.10, 0.38, "Track A separation"),
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new OperationProgress(0.38, "Separating Track B vocals"));
        var stemsB = await _separator.SeparateAsync(
            analyzedB,
            Path.Combine(workspaceDirectory, "track-b"),
            new ProgressScope(progress, 0.38, 0.66, "Track B separation"),
            cancellationToken).ConfigureAwait(false);

        // Этап 5: улучшение извлечённых стемов. Обрабатываем только те файлы,
        // которые реально участвуют в мэшапе: инструментал A и вокал B.
        // Цепочки мягкие, но заметно убирают гул, муть и провал разборчивости.
        var enhancedStemsA = stemsA;
        var enhancedStemsB = stemsB;
        if (enhanceStems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress(0.66, "Enhancing instrumental"));
            var instrumentalPath = Path.Combine(
                workspaceDirectory,
                "track-a",
                "instrumental-enhanced.wav");
            var enhancedInstrumentalPath = await _stemEnhancer.EnhanceAsync(
                stemsA.InstrumentalPath,
                instrumentalPath,
                AudioStemKind.Instrumental,
                analyzedA.Duration,
                new ProgressScope(progress, 0.66, 0.75, "Instrumental enhancement"),
                cancellationToken).ConfigureAwait(false);
            enhancedStemsA = new StemSeparationResult(
                stemsA.SourceFilePath,
                enhancedInstrumentalPath,
                stemsA.VocalsPath);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress(0.75, "Enhancing vocal"));
            var vocalPath = Path.Combine(
                workspaceDirectory,
                "track-b",
                "vocals-enhanced.wav");
            var enhancedVocalPath = await _stemEnhancer.EnhanceAsync(
                stemsB.VocalsPath,
                vocalPath,
                AudioStemKind.Vocal,
                analyzedB.Duration,
                new ProgressScope(progress, 0.75, 0.84, "Vocal enhancement"),
                cancellationToken).ConfigureAwait(false);
            enhancedStemsB = new StemSeparationResult(
                stemsB.SourceFilePath,
                stemsB.InstrumentalPath,
                enhancedVocalPath);
        }
        else
        {
            // Пропускаем зарезервированный диапазон, чтобы прогресс сразу
            // перешёл к выравниванию.
            progress?.Report(new OperationProgress(0.84, "Stem enhancement skipped"));
        }

        // Этап 6: чистая математика выравнивания. Здесь ещё ничего не рендерится:
        // получаем коэффициент темпа, сдвиг тональности и стартовое смещение.
        cancellationToken.ThrowIfCancellationRequested();
        var alignment = AlignmentCalculator.Calculate(analyzedA, analyzedB);
        var alignedVocalPath = Path.Combine(
            workspaceDirectory,
            $"aligned-vocals-{Guid.NewGuid():N}.wav");

        // Этап 7: единственный тяжёлый рендер. Новое имя файла на каждый запуск
        // не даёт неудачной попытке испортить уже готовый вокал.
        progress?.Report(new OperationProgress(0.84, "Applying tempo and key alignment"));
        await _processing.RenderAlignedVocalsAsync(
            enhancedStemsB.VocalsPath,
            alignedVocalPath,
            alignment.Parameters,
            analyzedB.Duration,
            new ProgressScope(progress, 0.84, 0.99, "Vocal alignment"),
            cancellationToken).ConfigureAwait(false);

        var session = new MashupSession(
            analyzedA,
            analyzedB,
            enhancedStemsA,
            enhancedStemsB,
            alignedVocalPath);
        progress?.Report(OperationProgress.Complete("Mashup prepared"));
        return new MashupPreparationResult(session, alignment);
    }

    public async Task<AlignedVocalResult> RenderAlignedVocalAsync(
        MashupSession session,
        double finePitchSemitones,
        TimeSpan offsetNudge,
        string outputFilePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        double? playbackSpeedRatioOverride = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);

        // Повторный рендер всегда считается от исходных данных сессии, а не от
        // предыдущего результата. Поэтому +0.5 и затем +1.0 полутона не сложатся
        // в +1.5: итоговый сдвиг останется +1.0.
        // Ручные поправки BPM, первой доли и тональности уже учтены в
        // Effective-дорожках; без поправок это исходный анализ.
        var alignment = AlignmentCalculator.Calculate(
            session.EffectiveInstrumentalTrack,
            session.EffectiveVocalTrack,
            finePitchSemitones,
            offsetNudge,
            playbackSpeedRatioOverride);

        progress?.Report(new OperationProgress(0.01, "Applying fine pitch and offset"));
        await _processing.RenderAlignedVocalsAsync(
            session.SourceVocalPath,
            outputFilePath,
            alignment.Parameters,
            session.VocalTrack.Duration,
            new ProgressScope(progress, 0.02, 0.99, "Vocal alignment"),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(OperationProgress.Complete("Fine tuning applied"));
        return new AlignedVocalResult(Path.GetFullPath(outputFilePath), alignment);
    }

    private async Task<AudioTrack> AnalyzeAsync(
        AudioTrack track,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var analysis = await _analyzer.AnalyzeAsync(track.FilePath, progress, cancellationToken)
            .ConfigureAwait(false);
        return track.WithAnalysis(analysis);
    }

    private static void EnsureReadable(string filePath, string role)
    {
        if (!File.Exists(filePath))
        {
            throw new AudioProcessingException($"{role} file does not exist: {filePath}");
        }
    }

    /// <summary>
    /// Переводит локальный прогресс дочернего сервиса (0..1) в его долю
    /// общего прогресса конвейера. Например, 0.5 при диапазоне 10–42 %
    /// превращается в 26 %.
    /// </summary>
    private sealed class ProgressScope : IProgress<OperationProgress>
    {
        private readonly IProgress<OperationProgress>? _inner;
        private readonly double _start;
        private readonly double _end;
        private readonly string _label;

        public ProgressScope(
            IProgress<OperationProgress>? inner,
            double start,
            double end,
            string label)
        {
            _inner = inner;
            _start = start;
            _end = end;
            _label = label;
        }

        public void Report(OperationProgress value)
        {
            if (_inner is null)
            {
                return;
            }

            // Clamp защищает общий индикатор от «скачков назад», если инструмент
            // пришлёт значение чуть ниже нуля или выше единицы.
            var fraction = _start + ((_end - _start) * Math.Clamp(value.Fraction, 0, 1));
            var message = string.IsNullOrWhiteSpace(value.Message)
                ? _label
                : $"{_label}: {value.Message}";

            _inner.Report(new OperationProgress(fraction, message));
        }
    }
}

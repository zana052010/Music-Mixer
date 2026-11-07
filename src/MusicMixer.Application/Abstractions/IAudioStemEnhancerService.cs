// Порт улучшения извлечённых стемов.
// Слой Application: сценарий не знает, чем именно чистится звук.
using MusicMixer.Domain.Models;

namespace MusicMixer.Application.Abstractions;

/// <summary>
/// Роль стема: для инструментала и вокала применяются разные цепочки обработки.
/// </summary>
public enum AudioStemKind
{
    Instrumental,
    Vocal,
}

/// <summary>
/// Постобработка стема после разделения: убирает гул и муть, добавляет
/// разборчивость и мягко выравнивает громкость.
/// </summary>
public interface IAudioStemEnhancerService
{
    /// <summary>
    /// Обрабатывает стем и возвращает путь к готовому WAV. Реализация обязана
    /// либо создать outputFilePath, либо вернуть существующий sourceFilePath
    /// (например, если платформа пока не поддерживает улучшение).
    /// </summary>
    Task<string> EnhanceAsync(
        string sourceFilePath,
        string outputFilePath,
        AudioStemKind kind,
        TimeSpan sourceDuration,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

// Порт главного сценария: UI зависит от этого интерфейса, а не от конкретной
// реализации MashupWorkflow. Позволяет подменить конвейер в тестах.
using MusicMixer.Domain.Models;

namespace MusicMixer.Application.Mashup;

public interface IMashupWorkflow
{
    /// <summary>
    /// Полный цикл: анализ A и B, разделение стемов, необязательное улучшение
    /// инструментала и вокала, расчёт выравнивания и рендеринг вокала.
    /// Возвращает готовую сессию для предпросмотра.
    /// </summary>
    Task<MashupPreparationResult> PrepareAsync(
        AudioTrack trackA,
        AudioTrack trackB,
        string workspaceDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool enhanceStems = true);

    /// <summary>
    /// Пересобирает вокал из исходного стема с новыми параметрами точной
    /// высоты тона и синхро-сдвига. Используется после движения ползунков.
    /// </summary>
    Task<AlignedVocalResult> RenderAlignedVocalAsync(
        MashupSession session,
        double finePitchSemitones,
        TimeSpan offsetNudge,
        string outputFilePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        double? playbackSpeedRatioOverride = null);
}

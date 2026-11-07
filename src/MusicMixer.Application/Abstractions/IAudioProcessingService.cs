// Порт рендеринга вокала. Реализация — RubberBandAlignmentService (FFmpeg),
// вызывается из MashupWorkflow после расчёта AlignmentParameters.
using MusicMixer.Domain.Models;

namespace MusicMixer.Application.Abstractions;

public interface IAudioProcessingService
{
    /// <summary>
    /// Рендерит вокальный стем с изменением темпа и высоты тона через фильтр
    /// librubberband в FFmpeg. Возвращённый путь обязан указывать на готовый WAV.
    /// </summary>
    Task<string> RenderAlignedVocalsAsync(
        string vocalFilePath,
        string outputFilePath,
        AlignmentParameters parameters,
        TimeSpan sourceDuration,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

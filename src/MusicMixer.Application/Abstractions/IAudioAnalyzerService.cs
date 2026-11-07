// Порт анализа аудио. Реализация — AudioAnalyzerService в Infrastructure;
// вызывается из MashupWorkflow до разделения стемов.
using MusicMixer.Domain.Models;

namespace MusicMixer.Application.Abstractions;

public interface IAudioAnalyzerService
{
    /// <summary>
    /// Определяет длительность, BPM, первый даунбит 4/4 и тональность.
    /// Реализация не должна выполнять ресурсоёмкую работу в потоке UI.
    /// </summary>
    Task<AudioAnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

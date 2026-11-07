// Порт сведения готового мэшапа в один файл.
// Слой Application: UI не должен знать, чем именно сводится аудио
// (FFmpeg, BASS или другой движок). Реализация — FfmpegMixdownService.
using MusicMixer.Domain.Models;

namespace MusicMixer.Application.Abstractions;

public interface IMixdownService
{
    /// <summary>
    /// Сводит инструментал и уже выровненный вокал в один WAV, применяя
    /// стартовое смещение вокала и текущие громкости дорожек.
    /// </summary>
    /// <param name="instrumentalFilePath">Путь к инструментальному стему.</param>
    /// <param name="vocalFilePath">Путь к выровненному вокальному стему.</param>
    /// <param name="vocalStartOffset">Смещение вокала относительно инструментала.</param>
    /// <param name="instrumentalVolume">Громкость инструментала в диапазоне 0..1.</param>
    /// <param name="vocalVolume">Громкость вокала в диапазоне 0..1.</param>
    /// <param name="expectedDuration">Ожидаемая длительность для расчёта прогресса.</param>
    /// <param name="outputFilePath">Куда сохранить готовый WAV.</param>
    Task<string> ExportAsync(
        string instrumentalFilePath,
        string vocalFilePath,
        TimeSpan vocalStartOffset,
        double instrumentalVolume,
        double vocalVolume,
        TimeSpan expectedDuration,
        string outputFilePath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

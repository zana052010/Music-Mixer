// Порт точной обрезки исходника. Реализация — FfmpegAudioTrimmerService;
// вызывается из AudioTrimViewModel до анализа и разделения.
using MusicMixer.Domain.Models;

namespace MusicMixer.Application.Abstractions;

public interface IAudioTrimmerService
{
    /// <summary>
    /// Вырезает диапазон start..end в отдельный WAV с точностью до сэмпла.
    /// Исходный файл не изменяется.
    /// </summary>
    Task<string> TrimAsync(
        string sourceFilePath,
        string outputFilePath,
        TimeSpan start,
        TimeSpan end,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

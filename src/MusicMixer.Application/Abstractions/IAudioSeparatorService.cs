// Порт разделения на стемы. Реализация — DemucsCliSeparatorService;
// вызывается из MashupWorkflow.
using MusicMixer.Domain.Models;

namespace MusicMixer.Application.Abstractions;

public interface IAudioSeparatorService
{
    /// <summary>
    /// Разделяет исходный файл на вокальный и инструментальный стемы.
    /// Реализация владеет временем жизни процесса и обязана убить дерево
    /// процессов при отмене cancellationToken.
    /// </summary>
    Task<StemSeparationResult> SeparateAsync(
        AudioTrack source,
        string outputDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

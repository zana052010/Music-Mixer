// Порт извлечения встроенной обложки трека. Реализация — FfmpegCoverArtService;
// вызывается из MainViewModel после выбора трека, чтобы показать обложку в карточке.
namespace MusicMixer.Application.Abstractions;

public interface ICoverArtService
{
    /// <summary>
    /// Достаёт встроенную обложку аудиофайла (MP3/FLAC/M4A/OGG) в квадратный PNG.
    /// Возвращает путь к картинке или null, если обложки нет.
    /// </summary>
    Task<string?> ExtractEmbeddedCoverAsync(
        string audioFilePath,
        string outputFilePath,
        CancellationToken cancellationToken = default);
}

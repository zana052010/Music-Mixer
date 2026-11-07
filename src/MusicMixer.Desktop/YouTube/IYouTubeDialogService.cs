// Порт показа модального окна YouTube. Слой Desktop.
// Скрывает от MainViewModel создание окна, владельца и жизненный цикл.
namespace MusicMixer.Desktop.YouTube;

public interface IYouTubeDialogService
{
    /// <summary>
    /// Открывает диалог и возвращает выбранный трек или null при отмене.
    /// outputDirectory — рабочая область, куда будет скачан WAV.
    /// </summary>
    Task<YouTubeIngestionResult?> ShowAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default);
}

// Порт поиска и загрузки с YouTube. Реализация — YoutubeExplodeTrackService
// (YoutubeExplode + FFmpeg).
using MusicMixer.Application.YouTube;

namespace MusicMixer.Application.Abstractions;

public interface IYouTubeTrackService
{
    /// <summary>
    /// True, если реализация умеет читать пользовательский файл cookies.
    /// </summary>
    bool SupportsCookieFile { get; }

    /// <summary>
    /// Текущий файл cookies, если он задан явно или через окружение.
    /// </summary>
    string? CookieFilePath { get; }

    /// <summary>
    /// Задаёт путь к cookies.txt в формате Netscape или сбрасывает его.
    /// Используется, когда cookies браузера недоступны.
    /// </summary>
    void SetCookieFile(string? filePath);

    /// <summary>
    /// Ищет видео и возвращает лёгкие метаданные без скачивания медиа.
    /// </summary>
    Task<IReadOnlyList<YouTubeTrackItem>> SearchTracksAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Скачивает аудиопоток с максимальным битрейтом и конвертирует его в PCM WAV.
    /// При отмене реализация обязана удалить частичные файлы.
    /// </summary>
    Task DownloadAudioStreamAsync(
        string videoId,
        string outputFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

// Элемент результатов поиска YouTube. Слой Application: не содержит
// клиента YouTube или путей к скачанным файлам.
namespace MusicMixer.Application.YouTube;

/// <summary>
/// Лёгкий результат поиска. Медиа не скачивается, пока пользователь не выберет
/// элемент; поэтому объект хранит только метаданные и URL миниатюры.
/// </summary>
public sealed record YouTubeTrackItem
{
    public YouTubeTrackItem(
        string id,
        string title,
        string author,
        TimeSpan? duration,
        string thumbnailUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        Id = id.Trim();
        Title = title.Trim();
        // Автор и миниатюра могут отсутствовать в ответе API: подставляем
        // безопасные значения, чтобы строка результата всегда отрисовалась.
        Author = string.IsNullOrWhiteSpace(author) ? "YouTube" : author.Trim();
        Duration = duration;
        ThumbnailUrl = thumbnailUrl?.Trim() ?? string.Empty;
    }

    public string Id { get; }

    public string Title { get; }

    public string Author { get; }

    public TimeSpan? Duration { get; }

    public string ThumbnailUrl { get; }
}

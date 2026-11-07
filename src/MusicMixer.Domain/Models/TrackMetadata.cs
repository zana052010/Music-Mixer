// Метаданные трека: название, исполнитель, альбом. Слой Domain.
// Единственный гарантированно доступный источник — имя файла, поэтому
// TrackMetadata.FromFilePath разбирает шаблон "Исполнитель - Название".
namespace MusicMixer.Domain.Models;

/// <summary>
/// Минимальные неизменяемые метаданные. Имя файла — единственный источник,
/// доступный без дополнительной библиотеки чтения тегов.
/// </summary>
public sealed record TrackMetadata
{
    public TrackMetadata(string title, string? artist = null, string? album = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A track title is required.", nameof(title));
        }

        Title = title.Trim();
        Artist = string.IsNullOrWhiteSpace(artist) ? null : artist.Trim();
        Album = string.IsNullOrWhiteSpace(album) ? null : album.Trim();
    }

    public string Title { get; }

    public string? Artist { get; }

    public string? Album { get; }

    public static TrackMetadata FromFilePath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var stem = Path.GetFileNameWithoutExtension(filePath);
        // Поддерживаем распространённый шаблон "Artist - Title". Если
        // разделителя нет или он в начале/конце, всё имя считается названием.
        var separatorIndex = stem.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex > 0 && separatorIndex < stem.Length - 3)
        {
            return new TrackMetadata(
                stem[(separatorIndex + 3)..],
                stem[..separatorIndex]);
        }

        return new TrackMetadata(stem);
    }
}

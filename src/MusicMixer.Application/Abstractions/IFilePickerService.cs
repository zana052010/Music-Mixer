// Порт выбора локального файла. Скрывает от ViewModel конкретный
// диалог Avalonia StorageProvider.
namespace MusicMixer.Application.Abstractions;

public interface IFilePickerService
{
    /// <summary>
    /// Показывает системный выбор файла. Возвращает null, если пользователь
    /// закрыл диалог без выбора.
    /// </summary>
    Task<string?> PickAudioFileAsync(
        string dialogTitle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Показывает выбор файла cookies.txt. Возвращает null, если пользователь
    /// закрыл диалог или платформа не поддерживает cookies-файлы.
    /// </summary>
    Task<string?> PickCookiesFileAsync(
        string dialogTitle,
        CancellationToken cancellationToken = default);
}

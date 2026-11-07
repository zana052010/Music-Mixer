// Адаптер Avalonia StorageProvider к интерфейсу IFilePickerService.
// Слой Desktop: превращает системный диалог выбора файла в путь или null.
// Вызывается MainViewModel при выборе трека A или B.
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using MusicMixer.Application.Abstractions;

namespace MusicMixer.Desktop.Services;

/// <summary>
/// Показывает нативный диалог выбора аудиофайла и возвращает локальный путь.
/// </summary>
public sealed class StorageProviderFilePickerService : IFilePickerService
{
    private static readonly FilePickerFileType AudioFileType = new("Audio files")
    {
        Patterns =
        [
            "*.wav", "*.mp3", "*.flac", "*.m4a", "*.aac",
            "*.ogg", "*.opus", "*.aiff", "*.aif", "*.wma",
        ],
        MimeTypes =
        [
            "audio/wav", "audio/x-wav", "audio/mpeg", "audio/flac",
            "audio/mp4", "audio/aac", "audio/ogg", "audio/opus",
            "audio/aiff", "audio/x-aiff", "audio/x-ms-wma",
        ],
    };

    private static readonly FilePickerFileType CookiesFileType = new("Cookies files")
    {
        Patterns = ["*.txt", "*.cookies"],
        MimeTypes = ["text/plain"],
    };

    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;

    public StorageProviderFilePickerService(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    /// <summary>
    /// Открывает диалог с фильтром аудиоформатов. При отмене пользователем
    /// или для файла без локального пути возвращает null.
    /// </summary>
    public async Task<string?> PickAudioFileAsync(
        string dialogTitle,
        CancellationToken cancellationToken = default)
    {
        // StorageProvider не принимает CancellationToken, поэтому отмена
        // проверяется до и после диалога, а не во время его показа.
        cancellationToken.ThrowIfCancellationRequested();

        var window = _lifetime.MainWindow
            ?? throw new InvalidOperationException("The main window is not available yet.");

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = dialogTitle,
            AllowMultiple = false,
            FileTypeFilter = [AudioFileType],
        });

        cancellationToken.ThrowIfCancellationRequested();
        // TryGetLocalPath вернёт null для нелокальных файлов (например, из облака),
        // и вызывающая сторона обработает это как отмену выбора.
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickCookiesFileAsync(
        string dialogTitle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var window = _lifetime.MainWindow
            ?? throw new InvalidOperationException("The main window is not available yet.");

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = dialogTitle,
            AllowMultiple = false,
            FileTypeFilter = [CookiesFileType],
        });

        cancellationToken.ThrowIfCancellationRequested();
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}

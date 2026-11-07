// Диалог сохранения готового микса. Слой Desktop.
// Скрывает от MainViewModel Avalonia StorageProvider и детали окна-владельца.
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace MusicMixer.Desktop.Services;

public sealed class AudioExportDialogService : IAudioExportDialogService
{
    private static readonly FilePickerFileType WaveFileType = new("WAV audio")
    {
        Patterns = ["*.wav"],
        MimeTypes = ["audio/wav", "audio/x-wav"],
    };

    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;

    public AudioExportDialogService(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    public async Task<string?> PickOutputPathAsync(
        string suggestedFileName,
        string dialogTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(dialogTitle);

        // StorageProvider не принимает CancellationToken, поэтому отмена
        // проверяется до и после показа диалога.
        cancellationToken.ThrowIfCancellationRequested();

        var window = _lifetime.MainWindow
            ?? throw new InvalidOperationException("The main window is not available yet.");

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = dialogTitle,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "wav",
            ShowOverwritePrompt = true,
            FileTypeChoices = [WaveFileType],
        });

        cancellationToken.ThrowIfCancellationRequested();

        // TryGetLocalPath вернёт null для нелокального расположения;
        // для вызывающей стороны это равносильно отмене.
        return file?.TryGetLocalPath();
    }
}

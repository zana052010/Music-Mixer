// Порт выбора пути для сохранения готового микса.
// Слой Desktop.
namespace MusicMixer.Desktop.Services;

public interface IAudioExportDialogService
{
    /// <summary>
    /// Показывает системный диалог сохранения WAV. Возвращает null,
    /// если пользователь отменил выбор.
    /// </summary>
    Task<string?> PickOutputPathAsync(
        string suggestedFileName,
        string dialogTitle,
        CancellationToken cancellationToken = default);
}

// Desktop-слой: контракты и DTO диалога прослушивания перед добавлением трека.
// Запрос несёт путь к файлу и готовые подписи, ответ — только решение пользователя.
// Используется AudioAuditionDialogService и вызывающим кодом MainViewModel.
namespace MusicMixer.Desktop.Audition;

/// <summary>
/// Запрос на прослушивание: FilePath — исходный аудиофайл, Title и Subtitle —
/// готовые подписи для шапки диалога, без дополнительной локализации внутри VM.
/// </summary>
public sealed record AudioAuditionRequest(
    string FilePath,
    string Title,
    string Subtitle);

/// <summary>
/// Контракт модального диалога прослушивания: true подтверждает добавление,
/// false означает отмену или закрытие окна.
/// </summary>
public interface IAudioAuditionDialogService
{
    /// <summary>
    /// Показывает диалог и ждёт решения; отмена токена закрывает окно как отказ.
    /// </summary>
    Task<bool> ShowAsync(
        AudioAuditionRequest request,
        CancellationToken cancellationToken = default);
}

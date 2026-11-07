// Порт извлечения пиков волновой формы для дорожек и диалогов.
// Слой Application: UI получает нормализованный массив 0..1 и не знает,
// каким декодером он получен.
namespace MusicMixer.Application.Abstractions;

public interface IAudioWaveformService
{
    /// <summary>
    /// Возвращает не более <paramref name="bucketCount"/> пиков амплитуды
    /// в диапазоне 0..1. Пустой массив означает, что в файле нет аудио.
    /// Ошибки декодирования пробрасываются как обычные исключения:
    /// волновая форма не критична для воспроизведения, и вызывающий UI
    /// может оставить прежнее изображение.
    /// </summary>
    Task<IReadOnlyList<float>> ExtractPeaksAsync(
        string filePath,
        int bucketCount,
        CancellationToken cancellationToken = default);
}

// Desktop-слой: DTO диалога обрезки — запрос диапазона и подтверждённое выделение.
// Используется AudioTrimDialogService, AudioTrimViewModel и MainViewModel.
// Хранит только временные метки; сам звук режет вызывающий код через FFmpeg.
namespace MusicMixer.Desktop.Trimming;

/// <summary>
/// Запрос на обрезку: FilePath — исходник, RoleLabel — подпись роли (инструментал или вокал),
/// ExistingStart и ExistingEnd позволяют открыть диалог с уже сохранённым диапазоном.
/// </summary>
public sealed record AudioTrimRequest(
    string FilePath,
    string Title,
    string RoleLabel,
    TimeSpan? ExistingStart = null,
    TimeSpan? ExistingEnd = null);

/// <summary>
/// Подтверждённое выделение в абсолютных секундах исходного файла.
/// FullDuration нужен, чтобы вызывающий код мог пересчитать таймлайн после обрезки.
/// </summary>
public sealed record AudioTrimSelection
{
    public AudioTrimSelection(TimeSpan start, TimeSpan end, TimeSpan fullDuration)
    {
        // Инварианты проверяем здесь, чтобы FFmpeg не получил отрицательную или пустую позицию.
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        // Пустой диапазон не имеет смысла: длина выделения должна быть положительной.
        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        // Конец не может выходить за файл, иначе обрезка даст тишину в хвосте.
        if (fullDuration < end)
        {
            throw new ArgumentOutOfRangeException(nameof(fullDuration));
        }

        Start = start;
        End = end;
        FullDuration = fullDuration;
    }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public TimeSpan FullDuration { get; }

    public TimeSpan Duration => End - Start;
}

public interface IAudioTrimDialogService
{
    Task<AudioTrimSelection?> ShowAsync(
        AudioTrimRequest request,
        CancellationToken cancellationToken = default);
}

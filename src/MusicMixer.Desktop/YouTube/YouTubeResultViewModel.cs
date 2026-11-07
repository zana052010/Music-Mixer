// Строка результата поиска YouTube.
// Слой Desktop. Хранит метаданные, миниатюру и две команды назначения —
// на дорожку A (инструментал) или B (вокал).
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicMixer.Application.YouTube;

namespace MusicMixer.Desktop.YouTube;

public sealed partial class YouTubeResultViewModel : ObservableObject, IDisposable
{
    // Колбэки родительской ViewModel: она владеет загрузкой и общим
    // состоянием «идёт скачивание», поэтому решение принимает не строка.
    private readonly Func<YouTubeResultViewModel, TrackTarget, Task> _selectAsync;
    private readonly Func<bool> _canSelect;

    public YouTubeResultViewModel(
        YouTubeTrackItem track,
        int index,
        Func<YouTubeResultViewModel, TrackTarget, Task> selectAsync,
        Func<bool> canSelect)
    {
        Track = track;
        _selectAsync = selectAsync;
        _canSelect = canSelect;
        TrackNumber = (index + 1).ToString("00");
        Initials = CreateInitials(track.Title);
        // Обе команды используют одну проверку доступности: пока идёт
        // скачивание, выбрать нельзя ни A, ни B.
        SetAsTrackACommand = new AsyncRelayCommand(
            () => _selectAsync(this, TrackTarget.Instrumental),
            _canSelect);
        SetAsTrackBCommand = new AsyncRelayCommand(
            () => _selectAsync(this, TrackTarget.Vocals),
            _canSelect);
    }

    public YouTubeTrackItem Track { get; }

    public string TrackNumber { get; }

    public string Title => Track.Title;

    public string Author => Track.Author;

    public string DurationText => Track.Duration is { } duration
        ? FormatDuration(duration)
        : "--:--";

    public string Initials { get; }

    public string ThumbnailUrl => Track.ThumbnailUrl;

    public IAsyncRelayCommand SetAsTrackACommand { get; }

    public IAsyncRelayCommand SetAsTrackBCommand { get; }

    [ObservableProperty]
    private Bitmap? thumbnail;

    public void NotifySelectionState()
    {
        // Вызывается родителем, когда начинается или заканчивается загрузка.
        SetAsTrackACommand.NotifyCanExecuteChanged();
        SetAsTrackBCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        // Bitmap держит нативную память, поэтому освобождаем явно.
        var bitmap = Thumbnail;
        Thumbnail = null;
        bitmap?.Dispose();
    }

    /// <summary>
    /// Заглушка вместо миниатюры: первые две буквы названия. Нужна, когда
    /// картинка ещё грузится или YouTube не отдал URL.
    /// </summary>
    private static string CreateInitials(string title)
    {
        var words = title.Split(
            [' ', '-', '—', ':', '(', ')', ',', '.'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 0)
        {
            return "YT";
        }

        if (words.Length == 1)
        {
            return words[0].Length >= 2
                ? words[0][..2].ToUpperInvariant()
                : words[0].ToUpperInvariant();
        }

        return string.Concat(
            words[0][0].ToString().ToUpperInvariant(),
            words[1][0].ToString().ToUpperInvariant());
    }

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");
}

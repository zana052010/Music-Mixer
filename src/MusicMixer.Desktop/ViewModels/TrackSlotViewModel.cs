// Модель одной исходной дорожки (A или B) для карточки в UI.
// Слой Desktop. Хранит трек, путь к исходнику, диапазон обрезки и
// локализованные подписи; сам звук не обрабатывает.
using CommunityToolkit.Mvvm.ComponentModel;
using MusicMixer.Desktop.Localization;
using MusicMixer.Domain.Models;

namespace MusicMixer.Desktop.ViewModels;

/// <summary>
/// Состояние одной дорожки: импортированный трек, исходный файл, применённая
/// обрезка и текстовые метки для карточки. При смене языка все метки
/// пересчитываются через ILocalizationService.
/// </summary>
public sealed partial class TrackSlotViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _localization;
    // Статус хранится ключом, а не готовым текстом: иначе при смене языка
    // пришлось бы обходить все возможные состояния.
    private string _statusKey = "StatusDropAudio";
    private string? _sourceFilePath;
    private TimeSpan? _trimStart;
    private TimeSpan? _trimEnd;

    public TrackSlotViewModel(ILocalizationService localization)
    {
        _localization = localization;
        // Карточка живёт всё время работы окна и должна обновлять подписи
        // при переключении RU/EN.
        _localization.LanguageChanged += OnLanguageChanged;
    }

    [ObservableProperty]
    private AudioTrack? track;

    public bool HasTrack => Track is not null;

    public bool IsEmpty => Track is null;

    // ---- Текстовые метки карточки -------------------------------------
    // Все они вычисляются из Track и текущего языка. SourceFilePath хранит
    // исходный файл до обрезки, поэтому имя в UI не меняется на временный WAV.

    public string DisplayTitle => Track?.Metadata.Title ?? _localization.GetString("NoTrackSelected");

    public string DisplaySubtitle => Track is not null
        ? Track.Metadata.Artist ?? _localization.GetString("LocalFile")
        : _localization.GetString("AudioFile");

    public string FileNameLabel => Track is null
        ? _localization.GetString("FileFormats")
        : Path.GetFileName(_sourceFilePath ?? Track.FilePath);

    public string? OriginalFilePath => _sourceFilePath;

    public TimeSpan? TrimStart => _trimStart;

    public TimeSpan? TrimEnd => _trimEnd;

    public bool HasTrim => _trimStart is not null && _trimEnd is not null;

    public string TrimRangeLabel => HasTrim
        ? _localization.Format(
            "TrimRangeValue",
            FormatTrimTime(_trimStart!.Value),
            FormatTrimTime(_trimEnd!.Value),
            FormatTrimTime(_trimEnd.Value - _trimStart.Value))
        : string.Empty;

    public string DurationLabel => Track is { Duration.TotalSeconds: > 0 } track
        ? FormatDuration(track.Duration)
        : "--:--";

    public string BpmLabel => Track?.BeatsPerMinute is { } bpm
        ? _localization.Format("BpmValue", bpm)
        : _localization.GetString("BpmPlaceholder");

    public string KeyLabel => Track?.Key is { } key
        ? key.DisplayName.ToUpperInvariant()
        : _localization.GetString("KeyPlaceholder");

    public string CamelotLabel => Track?.Key?.Camelot ?? "—";

    public string StatusText => _localization.GetString(_statusKey);

    public bool HasAnalysis => Track?.HasAnalysis == true;

    public void SetStatus(string key)
    {
        _statusKey = key;
        OnPropertyChanged(nameof(StatusText));
    }

    public void SetImportedTrack(AudioTrack track, string sourceFilePath)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);

        // Новый импорт сбрасывает старую обрезку: диапазон относился к
        // предыдущему файлу.
        _sourceFilePath = Path.GetFullPath(sourceFilePath);
        _trimStart = null;
        _trimEnd = null;
        Track = track;
        NotifyTrimState();
    }

    /// <summary>
    /// Применяет результат обрезки: в слоте появляется новый укороченный трек,
    /// но исходный путь сохраняется для повторного открытия диалога.
    /// </summary>
    public void ApplyTrim(
        string sourceFilePath,
        AudioTrack trimmedTrack,
        TimeSpan start,
        TimeSpan end)
    {
        ArgumentNullException.ThrowIfNull(trimmedTrack);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);

        _sourceFilePath = Path.GetFullPath(sourceFilePath);
        _trimStart = start;
        _trimEnd = end;
        Track = trimmedTrack;
        NotifyTrimState();
    }

    /// <summary>
    /// Меняет содержимое двух слотов местами. Используется кнопкой между
    /// карточками A и B: трек, обрезка и статус переезжают в другой слот.
    /// </summary>
    public void SwapWith(TrackSlotViewModel other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
        {
            return;
        }

        var track = Track;
        Track = other.Track;
        other.Track = track;

        var sourcePath = _sourceFilePath;
        _sourceFilePath = other._sourceFilePath;
        other._sourceFilePath = sourcePath;

        var trimStart = _trimStart;
        _trimStart = other._trimStart;
        other._trimStart = trimStart;

        var trimEnd = _trimEnd;
        _trimEnd = other._trimEnd;
        other._trimEnd = trimEnd;

        var statusKey = _statusKey;
        _statusKey = other._statusKey;
        other._statusKey = statusKey;

        NotifyTrimState();
        other.NotifyTrimState();
        OnPropertyChanged(nameof(StatusText));
        other.OnPropertyChanged(nameof(StatusText));
    }

    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;

    partial void OnTrackChanged(AudioTrack? value)
    {
        // Source generator вызывает этот hook после смены Track.
        // Все производные свойства нужно уведомить вручную.
        OnPropertyChanged(nameof(HasTrack));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DisplaySubtitle));
        OnPropertyChanged(nameof(FileNameLabel));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(BpmLabel));
        OnPropertyChanged(nameof(KeyLabel));
        OnPropertyChanged(nameof(CamelotLabel));
        OnPropertyChanged(nameof(HasAnalysis));
    }

    private void OnLanguageChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DisplaySubtitle));
        OnPropertyChanged(nameof(FileNameLabel));
        OnPropertyChanged(nameof(BpmLabel));
        OnPropertyChanged(nameof(KeyLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(TrimRangeLabel));
    }

    private void NotifyTrimState()
    {
        OnPropertyChanged(nameof(OriginalFilePath));
        OnPropertyChanged(nameof(FileNameLabel));
        OnPropertyChanged(nameof(HasTrim));
        OnPropertyChanged(nameof(TrimRangeLabel));
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");

    private static string FormatTrimTime(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss\.ff")
            : duration.ToString(@"m\:ss\.ff");
}

// Модель одной исходной дорожки (A или B) для карточки в UI.
// Слой Desktop. Хранит трек, путь к исходнику, диапазон обрезки и
// локализованные подписи; сам звук не обрабатывает.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    // Поля редактора округляют BPM до сотых, а первую долю до миллисекунд;
    // разница меньше шага округления не считается ручной поправкой.
    private const double BpmTolerance = 0.006;
    private const double DownbeatToleranceMilliseconds = 0.6;

    // Ниже 50 % детектор часто путает половинный темп или параллельную
    // тональность, и карточка подсказывает проверить значения на слух.
    private const double LowConfidenceThreshold = 0.5;

    // Исходный результат анализа и ручные поправки к нему. Track хранит уже
    // применённые значения, а _automaticTrack нужен кнопке «Авто».
    private AudioTrack? _automaticTrack;
    private TrackAnalysisOverride _analysisOverride = TrackAnalysisOverride.None;
    private bool _isApplyingAnalysis;

    public TrackSlotViewModel(ILocalizationService localization)
    {
        _localization = localization;
        // Карточка живёт всё время работы окна и должна обновлять подписи
        // при переключении RU/EN.
        _localization.LanguageChanged += OnLanguageChanged;
    }

    [ObservableProperty]
    private AudioTrack? track;

    // ---- Редактор анализа ---------------------------------------------
    // Поля заполняются из Track и меняются пользователем; в трек они попадают
    // только после команды «Применить» в MainViewModel.

    [ObservableProperty]
    private decimal? editableBpm;

    [ObservableProperty]
    private decimal? editableDownbeatSeconds;

    [ObservableProperty]
    private KeyOption? editableKey;

    public IReadOnlyList<KeyOption> KeyOptions { get; } = KeyOption.All;

    public TrackAnalysisOverride AnalysisOverride => _analysisOverride;

    public bool IsAnalysisOverridden => !_analysisOverride.IsEmpty;

    public bool HasConfidence => Track is { TempoConfidence: not null, KeyConfidence: not null };

    public string ConfidenceLabel => Track is { TempoConfidence: { } tempo, KeyConfidence: { } key }
        ? _localization.Format("AnalysisConfidenceValue", tempo, key)
        : string.Empty;

    public bool IsLowConfidence =>
        Track is { TempoConfidence: { } tempo, KeyConfidence: { } key } &&
        (tempo < LowConfidenceThreshold || key < LowConfidenceThreshold);

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

    /// <summary>
    /// Показывает в карточке исходный анализ вместе с ручными поправками.
    /// Вызывается MainViewModel после успешного рендера с новыми значениями.
    /// </summary>
    public void SetAnalysis(AudioTrack automaticTrack, TrackAnalysisOverride analysisOverride)
    {
        ArgumentNullException.ThrowIfNull(automaticTrack);
        ArgumentNullException.ThrowIfNull(analysisOverride);

        _isApplyingAnalysis = true;
        try
        {
            _automaticTrack = automaticTrack;
            _analysisOverride = analysisOverride;
            Track = analysisOverride.ApplyTo(automaticTrack);
        }
        finally
        {
            _isApplyingAnalysis = false;
        }

        // Track мог не измениться (например, «Авто» без поправок), поэтому
        // поля редактора и флаги обновляются явно.
        LoadAnalysisEditor();
        OnPropertyChanged(nameof(AnalysisOverride));
        OnPropertyChanged(nameof(IsAnalysisOverridden));
    }

    /// <summary>
    /// Собирает поправки из полей редактора. Значение, совпадающее с
    /// автоматическим с точностью до округления в поле, поправкой не считается.
    /// </summary>
    public TrackAnalysisOverride BuildAnalysisOverride()
    {
        var automatic = _automaticTrack ?? Track;
        if (automatic is null)
        {
            return TrackAnalysisOverride.None;
        }

        double? bpm = EditableBpm is { } bpmValue
            ? Math.Clamp(
                (double)bpmValue,
                TrackAnalysisOverride.MinimumBeatsPerMinute,
                TrackAnalysisOverride.MaximumBeatsPerMinute)
            : null;
        if (bpm is { } requestedBpm &&
            automatic.BeatsPerMinute is { } automaticBpm &&
            Math.Abs(requestedBpm - automaticBpm) < BpmTolerance)
        {
            bpm = null;
        }

        TimeSpan? downbeat = EditableDownbeatSeconds is { } secondsValue
            ? TimeSpan.FromSeconds(Math.Clamp(
                (double)secondsValue,
                0,
                TrackAnalysisOverride.MaximumDownbeatOffset.TotalSeconds))
            : null;
        if (downbeat is { } requestedDownbeat &&
            automatic.DownbeatOffset is { } automaticDownbeat &&
            Math.Abs((requestedDownbeat - automaticDownbeat).TotalMilliseconds) < DownbeatToleranceMilliseconds)
        {
            downbeat = null;
        }

        var key = EditableKey?.Key;
        if (key is not null && key == automatic.Key)
        {
            key = null;
        }

        return new TrackAnalysisOverride(bpm, downbeat, key);
    }

    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;

    partial void OnTrackChanged(AudioTrack? value)
    {
        if (!_isApplyingAnalysis)
        {
            // Новый импорт, обрезка или обмен слотов: прежние поправки
            // относились к другому треку и сбрасываются.
            _automaticTrack = null;
            _analysisOverride = TrackAnalysisOverride.None;
        }

        LoadAnalysisEditor();
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
        OnPropertyChanged(nameof(AnalysisOverride));
        OnPropertyChanged(nameof(IsAnalysisOverridden));
        OnPropertyChanged(nameof(HasConfidence));
        OnPropertyChanged(nameof(ConfidenceLabel));
        OnPropertyChanged(nameof(IsLowConfidence));
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
        OnPropertyChanged(nameof(ConfidenceLabel));
    }

    // Кнопки ÷2 и ×2 исправляют типичную ошибку детектора — половинный
    // или двойной темп. Значение попадёт в трек после «Применить».
    [RelayCommand]
    private void HalveBpm() => ScaleEditableBpm(0.5m);

    [RelayCommand]
    private void DoubleBpm() => ScaleEditableBpm(2m);

    private void ScaleEditableBpm(decimal factor)
    {
        if (EditableBpm is not { } bpm)
        {
            return;
        }

        EditableBpm = Math.Clamp(
            Math.Round(bpm * factor, 2),
            (decimal)TrackAnalysisOverride.MinimumBeatsPerMinute,
            (decimal)TrackAnalysisOverride.MaximumBeatsPerMinute);
    }

    private void LoadAnalysisEditor()
    {
        var current = Track;
        EditableBpm = current?.BeatsPerMinute is { } bpm
            ? (decimal)Math.Round(bpm, 2)
            : null;
        EditableDownbeatSeconds = current?.DownbeatOffset is { } offset
            ? (decimal)Math.Round(offset.TotalSeconds, 3)
            : null;
        EditableKey = current?.Key is { } key
            ? KeyOptions.FirstOrDefault(option => option.Key == key)
            : null;
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

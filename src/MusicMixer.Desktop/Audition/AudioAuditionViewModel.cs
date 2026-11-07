// Desktop-слой: view model диалога прослушивания трека перед добавлением в мэшап.
// Управляет одиночным BASS-плеером через IAudioPlayerService: play/pause, seek,
// позиция и длительность; создаётся DI, вызывается AudioAuditionDialogService.
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicMixer.Application.Abstractions;
using MusicMixer.Desktop.Localization;

namespace MusicMixer.Desktop.Audition;

/// <summary>
/// Состояние диалога прослушивания: загружает один файл в источник предпросмотра,
/// синхронизирует ползунок с позицией плеера и сообщает результат через CloseRequested.
/// </summary>
public sealed partial class AudioAuditionViewModel : ObservableObject, IDisposable
{
    private readonly IAudioPlayerService _player;
    private readonly IAudioWaveformService _waveform;
    private readonly ILocalizationService _localization;
    private readonly DispatcherTimer _timer;

    private bool _updatingPlayhead;
    private bool _isUserSeeking;
    private bool _initialized;
    private bool _disposed;

    public AudioAuditionViewModel(
        IAudioPlayerService player,
        IAudioWaveformService waveform,
        ILocalizationService localization)
    {
        _player = player;
        _waveform = waveform;
        _localization = localization;
        // Подпись Play/Pause вычисляется из языка, поэтому её нужно обновлять при смене языка.
        _localization.LanguageChanged += OnLanguageChanged;

        // У BASS нет события прогресса: опрос 20 раз в секунду даёт плавный ползунок без лишней нагрузки.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTimerTick;
    }

    [ObservableProperty]
    private string trackTitle = string.Empty;

    [ObservableProperty]
    private string subtitle = string.Empty;

    [ObservableProperty]
    private string fileNameLabel = string.Empty;

    [ObservableProperty]
    private double durationSeconds;

    [ObservableProperty]
    private double playheadSeconds;

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private IReadOnlyList<float>? peaks;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string PlayheadTimeText => FormatTime(PlayheadSeconds);

    public string DurationText => FormatTime(DurationSeconds);

    /// <summary>
    /// Доля 0..1 для положения ползунка; при нулевой длительности возвращает 0,
    /// чтобы не делить на ноль.
    /// </summary>
    public double PlayheadFraction =>
        DurationSeconds > 0 ? Math.Clamp(PlayheadSeconds / DurationSeconds, 0, 1) : 0;

    public string PlayPauseText =>
        _localization.GetString(IsPlaying ? "Pause" : "Play");

    /// <summary>
    /// Единственный канал завершения диалога: окно подписывается на событие
    /// и закрывается с результатом true (добавить) или false (отмена).
    /// </summary>
    public event Action<bool>? CloseRequested;

    /// <summary>
    /// Проверяет файл, загружает его в источник предпросмотра и запускает таймер позиции.
    /// </summary>
    public async Task InitializeAsync(
        AudioAuditionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Проверяем до загрузки в BASS: иначе ошибка будет менее понятной, а плеер останется в промежуточном состоянии.
        if (!File.Exists(request.FilePath))
        {
            throw new FileNotFoundException(
                "The audition source file does not exist.",
                request.FilePath);
        }

        TrackTitle = request.Title;
        Subtitle = request.Subtitle;
        FileNameLabel = Path.GetFileName(request.FilePath);

        var duration = await _player.LoadSourcePreviewAsync(
            request.FilePath,
            cancellationToken);
        // Нулевая длительность означает, что BASS не нашёл декодируемого аудио в контейнере.
        if (duration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The source file has no playable audio.");
        }

        DurationSeconds = duration.TotalSeconds;
        _initialized = true;
        _timer.Start();
        // Ставим позицию в начало сразу, чтобы окно открылось с согласованными ползунком и текстом.
        SeekTo(0);
        NotifyPlayheadState();

        // Волновая форма не влияет на воспроизведение: если декодер не смог
        // её построить, диалог всё равно открывается с макетом.
        try
        {
            Peaks = await _waveform.ExtractPeaksAsync(
                request.FilePath,
                bucketCount: 640,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            Peaks = null;
        }
    }

    /// <summary>
    /// Переключает воспроизведение; если трек уже доигран, начинает его сначала.
    /// </summary>
    [RelayCommand]
    private void PlayPause()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            ClearError();
            if (IsPlaying)
            {
                _player.PauseSourcePreview();
                IsPlaying = false;
                return;
            }

            // Допуск 50 мс: позиция BASS редко приходит ровно на Duration, иначе трек не перезапустится.
            if (PlayheadSeconds >= DurationSeconds - 0.05)
            {
                SeekTo(0);
            }

            _player.PlaySourcePreview();
            IsPlaying = true;
        }
        catch (Exception exception)
        {
            HandlePlaybackError(exception);
        }
    }

    /// <summary>
    /// Останавливает воспроизведение и возвращает позицию в начало трека.
    /// </summary>
    [RelayCommand]
    private void Stop()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            _player.PauseSourcePreview();
            SeekTo(0);
            IsPlaying = false;
        }
        catch (Exception exception)
        {
            HandlePlaybackError(exception);
        }
    }

    /// <summary>
    /// Подтверждает добавление и просит окно закрыться с результатом true.
    /// </summary>
    [RelayCommand]
    private void Add() => CloseRequested?.Invoke(true);

    /// <summary>
    /// Отменяет прослушивание: окно закрывается с результатом false.
    /// </summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    /// <summary>
    /// Начало перетаскивания ползунка: до отпускания таймер не должен двигать позицию.
    /// </summary>
    public void BeginPlayheadSeek()
    {
        if (_initialized)
        {
            _isUserSeeking = true;
        }
    }

    /// <summary>
    /// Завершение перетаскивания: применяет выбранную позицию к плееру.
    /// </summary>
    public void EndPlayheadSeek()
    {
        if (!_isUserSeeking)
        {
            return;
        }

        _isUserSeeking = false;
        if (!_initialized)
        {
            return;
        }

        try
        {
            // Перемотку делаем один раз по отпусканию: BASS-перемотка дорогая и звук при частых seek «рвётся».
            _player.SeekSourcePreview(
                TimeSpan.FromSeconds(Math.Clamp(PlayheadSeconds, 0, DurationSeconds)));
        }
        catch (Exception exception)
        {
            HandlePlaybackError(exception);
        }
    }

    /// <summary>
    /// Останавливает таймер, отписывается от локализации и выгружает источник из BASS.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _localization.LanguageChanged -= OnLanguageChanged;

        try
        {
            _player.StopSourcePreview();
            _player.UnloadSourcePreview();
        }
        catch (Exception)
        {
            // При закрытии окна ошибку уже некому показать; важно освободить BASS и файловый дескриптор.
        }
    }

    partial void OnPlayheadSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PlayheadTimeText));
        OnPropertyChanged(nameof(PlayheadFraction));

        // _updatingPlayhead отсекает эхо от SetPlayhead, _isUserSeeking — обновления таймера при перетаскивании.
        if (!_initialized || _updatingPlayhead || _disposed)
        {
            return;
        }

        // Значение из слайдера могло выйти за пределы трека: возвращаем его в диапазон без лишнего seek.
        var clamped = Math.Clamp(value, 0, DurationSeconds);
        if (Math.Abs(clamped - value) > 1e-9)
        {
            SetPlayhead(clamped);
            return;
        }

        // Пока пользователь тащит ползунок, промежуточные значения не перематывают BASS — это сделает EndPlayheadSeek.
        if (_isUserSeeking)
        {
            return;
        }

        try
        {
            // Ручное изменение PlayheadSeconds (например, из слайдера) переводит и сам плеер.
            _player.SeekSourcePreview(TimeSpan.FromSeconds(clamped));
        }
        catch (Exception exception)
        {
            HandlePlaybackError(exception);
        }
    }

    partial void OnDurationSecondsChanged(double value)
    {
        // Длительность задаёт границы ползунка и текстов, поэтому пересчитываем зависимые свойства.
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(PlayheadFraction));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        // Подпись кнопки зависит от состояния, а не хранится отдельным полем.
        OnPropertyChanged(nameof(PlayPauseText));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    private void OnTimerTick(object? sender, EventArgs eventArgs)
    {
        if (!_initialized || _disposed)
        {
            return;
        }

        // Таймер — единственный источник позиции: BASS не присылает события прогресса.
        var actualPosition = _player.SourcePreviewPosition.TotalSeconds;
        if (!_isUserSeeking)
        {
            // Во время перетаскивания не перезаписываем позицию, иначе thumb «прыгает» под курсором.
            SetPlayhead(actualPosition);
        }

        // IsPlaying уточняем по фактическому состоянию плеера: трек мог закончиться сам.
        var playing = _player.IsSourcePreviewPlaying;
        if (IsPlaying != playing)
        {
            IsPlaying = playing;
        }
    }

    private void SeekTo(double seconds)
    {
        // Единая точка перемотки: сначала обновляем UI-позицию, затем сам плеер.
        var clamped = Math.Clamp(seconds, 0, DurationSeconds);
        SetPlayhead(clamped);
        _player.SeekSourcePreview(TimeSpan.FromSeconds(clamped));
    }

    private void SetPlayhead(double seconds)
    {
        // Флаг подавляет OnPlayheadSecondsChanged, чтобы обновление из таймера не вызвало обратный seek.
        _updatingPlayhead = true;
        PlayheadSeconds = Math.Clamp(seconds, 0, DurationSeconds);
        _updatingPlayhead = false;
    }

    private void NotifyPlayheadState()
    {
        // Avalonia не выводит зависимости вычисляемых свойств — уведомляем их вручную после загрузки.
        OnPropertyChanged(nameof(PlayheadTimeText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(PlayheadFraction));
    }

    private void HandlePlaybackError(Exception exception)
    {
        // Сбрасываем IsPlaying, чтобы кнопка вернулась в состояние Play, и показываем причину под плеером.
        IsPlaying = false;
        ErrorMessage = $"{_localization.GetString("ErrorPlaybackFailed")}{Environment.NewLine}{exception.Message}";
    }

    private void ClearError()
    {
        if (ErrorMessage is not null)
        {
            ErrorMessage = null;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs eventArgs)
    {
        // Язык влияет только на подпись Play/Pause: время и числа от локали не зависят.
        OnPropertyChanged(nameof(PlayPauseText));
    }

    private static string FormatTime(double seconds)
    {
        // Часы показываем только для треков от часа, чтобы не загромождать шапку коротких файлов.
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");
    }
}

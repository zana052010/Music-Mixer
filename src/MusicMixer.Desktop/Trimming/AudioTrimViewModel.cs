// ViewModel диалога обрезки источника.
// Слой Desktop. Загружает исходник в BASS, позволяет выбрать IN/OUT,
// прослушать выделение и вернуть диапазон в MainViewModel.
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicMixer.Application.Abstractions;
using MusicMixer.Desktop.Localization;

namespace MusicMixer.Desktop.Trimming;

/// <summary>
/// Состояние диалога обрезки: позиция курсора, границы IN/OUT, минимальная
/// длина фрагмента и команда применения. Сам звук не режет — возвращает
/// выбранный диапазон вызывающему коду.
/// </summary>
public sealed partial class AudioTrimViewModel : ObservableObject, IDisposable
{
    // Слишком короткий фрагмент бессмысленен для мэшапа и может дать
    // артефакты при анализе/растяжении.
    private const double MinimumSelectionSeconds = 4.0;

    private readonly IAudioPlayerService _player;
    private readonly IAudioWaveformService _waveform;
    private readonly ILocalizationService _localization;
    private readonly DispatcherTimer _timer;

    private AudioTrimRequest? _request;
    // Если задано, воспроизведение остановится на этом времени:
    // так работает «Прослушать выбранное».
    private TimeSpan? _selectionPreviewEnd;
    private bool _updatingPlayhead;
    private bool _isUserSeeking;
    private bool _initialized;
    private bool _disposed;

    public AudioTrimViewModel(
        IAudioPlayerService player,
        IAudioWaveformService waveform,
        ILocalizationService localization)
    {
        _player = player;
        _waveform = waveform;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTimerTick;
    }

    [ObservableProperty]
    private string trackTitle = string.Empty;

    [ObservableProperty]
    private string roleLabel = string.Empty;

    [ObservableProperty]
    private double durationSeconds;

    [ObservableProperty]
    private double startSeconds;

    [ObservableProperty]
    private double endSeconds;

    [ObservableProperty]
    private double playheadSeconds;

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private IReadOnlyList<float>? peaks;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>
    /// Кнопка «Применить» доступна только для готового файла и диапазона
    /// не короче минимума. Допуск 1 мс компенсирует погрешность double.
    /// </summary>
    public bool CanApplySelection =>
        _initialized &&
        DurationSeconds > 0 &&
        EndSeconds - StartSeconds >= MinimumSelectionSeconds - 0.001;

    public string StartTimeText => FormatTime(StartSeconds);

    public string EndTimeText => FormatTime(EndSeconds);

    public string PlayheadTimeText => FormatTime(PlayheadSeconds);

    public string DurationText => FormatTime(DurationSeconds);

    public string SelectionDurationText => FormatTime(Math.Max(0, EndSeconds - StartSeconds));

    public double PlayheadFraction =>
        DurationSeconds > 0 ? Math.Clamp(PlayheadSeconds / DurationSeconds, 0, 1) : 0;

    public double SelectionStartFraction =>
        DurationSeconds > 0 ? Math.Clamp(StartSeconds / DurationSeconds, 0, 1) : 0;

    public double SelectionEndFraction =>
        DurationSeconds > 0 ? Math.Clamp(EndSeconds / DurationSeconds, 0, 1) : 0;

    public string PlayPauseText =>
        _localization.GetString(IsPlaying ? "Pause" : "Play");

    public event Action<AudioTrimSelection?>? CloseRequested;

    public async Task InitializeAsync(
        AudioTrimRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(request.FilePath))
        {
            throw new FileNotFoundException("The trim source file does not exist.", request.FilePath);
        }

        _request = request;
        TrackTitle = request.Title;
        RoleLabel = request.RoleLabel;

        // BASS открывает исходник и сообщает полную длительность.
        var fullDuration = await _player.LoadSourcePreviewAsync(
            request.FilePath,
            cancellationToken);
        if (fullDuration.TotalSeconds < MinimumSelectionSeconds)
        {
            throw new InvalidOperationException(
                $"The source is too short to trim. Minimum length is {MinimumSelectionSeconds:0} seconds.");
        }

        DurationSeconds = fullDuration.TotalSeconds;
        // Повторное открытие диалога восстанавливает прежний диапазон,
        // но подгоняет его под текущий файл и минимальную длину.
        var initialStart = request.ExistingStart is { } existingStart
            ? Math.Clamp(existingStart.TotalSeconds, 0, DurationSeconds - MinimumSelectionSeconds)
            : 0;
        var initialEnd = request.ExistingEnd is { } existingEnd
            ? Math.Clamp(existingEnd.TotalSeconds, initialStart + MinimumSelectionSeconds, DurationSeconds)
            : DurationSeconds;

        StartSeconds = initialStart;
        EndSeconds = initialEnd;
        _initialized = true;
        _timer.Start();
        SeekTo(initialStart);
        NotifySelectionState();

        // Реальные пики делают IN/OUT визуально осмысленными; при сбое
        // остаётся детерминированный макет, а обрезка продолжает работать.
        try
        {
            Peaks = await _waveform.ExtractPeaksAsync(
                request.FilePath,
                bucketCount: 768,
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

            // Обычное воспроизведение не ограничено выделением:
            // пользователь может слушать весь трек.
            _selectionPreviewEnd = null;
            if (PlayheadSeconds >= DurationSeconds - 0.05)
            {
                SeekTo(StartSeconds);
            }

            _player.PlaySourcePreview();
            IsPlaying = true;
        }
        catch (Exception exception)
        {
            HandlePlaybackError(exception);
        }
    }

    [RelayCommand]
    private void Stop()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            // Останавливаем и возвращаем курсор в начало выделения,
            // чтобы следующее прослушивание началось оттуда же.
            _player.PauseSourcePreview();
            _player.SeekSourcePreview(TimeSpan.FromSeconds(StartSeconds));
            _selectionPreviewEnd = null;
            IsPlaying = false;
            SetPlayhead(StartSeconds);
        }
        catch (Exception exception)
        {
            HandlePlaybackError(exception);
        }
    }

    [RelayCommand]
    private void PreviewSelection()
    {
        if (!_initialized || !CanApplySelection)
        {
            return;
        }

        try
        {
            ClearError();
            SeekTo(StartSeconds);
            // Запоминаем конец выделения: таймер остановит плеер,
            // когда курсор дойдёт до него.
            _selectionPreviewEnd = TimeSpan.FromSeconds(EndSeconds);
            _player.PlaySourcePreview();
            IsPlaying = true;
        }
        catch (Exception exception)
        {
            HandlePlaybackError(exception);
        }
    }

    [RelayCommand]
    private void SetStartHere()
    {
        if (!_initialized)
        {
            return;
        }

        var newStart = Math.Clamp(
            PlayheadSeconds,
            0,
            Math.Max(0, DurationSeconds - MinimumSelectionSeconds));
        StartSeconds = newStart;
        // Если после переноса IN диапазон стал короче минимума,
        // подтягиваем OUT вперёд.
        if (EndSeconds - StartSeconds < MinimumSelectionSeconds)
        {
            EndSeconds = Math.Min(DurationSeconds, StartSeconds + MinimumSelectionSeconds);
        }

        NotifySelectionState();
    }

    [RelayCommand]
    private void SetEndHere()
    {
        if (!_initialized)
        {
            return;
        }

        var newEnd = Math.Clamp(PlayheadSeconds, MinimumSelectionSeconds, DurationSeconds);
        EndSeconds = newEnd;
        // Симметрично SetStartHere: если OUT слишком близко к IN,
        // сдвигаем IN назад, сохраняя минимальную длину.
        if (EndSeconds - StartSeconds < MinimumSelectionSeconds)
        {
            StartSeconds = Math.Max(0, EndSeconds - MinimumSelectionSeconds);
        }

        NotifySelectionState();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (_request is null ||
            EndSeconds - StartSeconds < MinimumSelectionSeconds - 0.001)
        {
            return;
        }

        CloseRequested?.Invoke(new AudioTrimSelection(
            TimeSpan.FromSeconds(StartSeconds),
            TimeSpan.FromSeconds(EndSeconds),
            TimeSpan.FromSeconds(DurationSeconds)));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    public void BeginPlayheadSeek()
    {
        if (_initialized)
        {
            _isUserSeeking = true;
        }
    }

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
            _player.SeekSourcePreview(
                TimeSpan.FromSeconds(Math.Clamp(PlayheadSeconds, 0, DurationSeconds)));
        }
        catch (Exception exception)
        {
            HandlePlaybackError(exception);
        }
    }

    private bool CanApply() => CanApplySelection;

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
            // Обязательно освобождаем BASS-поток и файловый дескриптор,
            // иначе следующий диалог не сможет открыть этот файл.
            _player.StopSourcePreview();
            _player.UnloadSourcePreview();
        }
        catch (Exception)
        {
        }
    }

    partial void OnPlayheadSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PlayheadTimeText));
        OnPropertyChanged(nameof(PlayheadFraction));

        if (!_initialized || _updatingPlayhead || _disposed)
        {
            return;
        }

        var clamped = Math.Clamp(value, 0, DurationSeconds);
        if (Math.Abs(clamped - value) > 1e-9)
        {
            SetPlayhead(clamped);
            return;
        }

        if (_isUserSeeking)
        {
            return;
        }

        try
        {
            _player.SeekSourcePreview(TimeSpan.FromSeconds(clamped));
        }
        catch (Exception exception)
        {
            HandlePlaybackError(exception);
        }
    }

    partial void OnStartSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(StartTimeText));
        OnPropertyChanged(nameof(SelectionDurationText));
        OnPropertyChanged(nameof(SelectionStartFraction));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnEndSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(EndTimeText));
        OnPropertyChanged(nameof(SelectionDurationText));
        OnPropertyChanged(nameof(SelectionEndFraction));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnDurationSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(PlayheadFraction));
        OnPropertyChanged(nameof(SelectionStartFraction));
        OnPropertyChanged(nameof(SelectionEndFraction));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPlayingChanged(bool value)
    {
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

        var actualPosition = _player.SourcePreviewPosition.TotalSeconds;
        if (!_isUserSeeking)
        {
            SetPlayhead(actualPosition);
        }

        var playing = _player.IsSourcePreviewPlaying;
        if (IsPlaying != playing)
        {
            IsPlaying = playing;
        }

        if (_selectionPreviewEnd is { } previewEnd &&
            actualPosition >= previewEnd.TotalSeconds - 0.005)
        {
            // Дошли до OUT: останавливаем «Прослушать выбранное».
            _player.PauseSourcePreview();
            _selectionPreviewEnd = null;
            IsPlaying = false;
        }
    }

    private void SeekTo(double seconds)
    {
        var clamped = Math.Clamp(seconds, 0, DurationSeconds);
        SetPlayhead(clamped);
        _player.SeekSourcePreview(TimeSpan.FromSeconds(clamped));
    }

    private void SetPlayhead(double seconds)
    {
        // _updatingPlayhead подавляет обратный seek из OnPlayheadSecondsChanged:
        // обновление из таймера не должно выглядеть как действие пользователя.
        _updatingPlayhead = true;
        PlayheadSeconds = Math.Clamp(seconds, 0, DurationSeconds);
        _updatingPlayhead = false;
    }

    private void NotifySelectionState()
    {
        OnPropertyChanged(nameof(StartTimeText));
        OnPropertyChanged(nameof(EndTimeText));
        OnPropertyChanged(nameof(SelectionDurationText));
        OnPropertyChanged(nameof(SelectionStartFraction));
        OnPropertyChanged(nameof(SelectionEndFraction));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void HandlePlaybackError(Exception exception)
    {
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
        OnPropertyChanged(nameof(PlayPauseText));
    }

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss\.ff")
            : value.ToString(@"m\:ss\.ff");
    }
}

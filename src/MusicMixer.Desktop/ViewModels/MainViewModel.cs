// Центральная ViewModel главного экрана.
// Слой Desktop. Связывает UI с рабочим процессом, плеером, диалогами и
// локализацией: хранит состояние сессии, команды и тексты статусов.
// Главный сценарий: импорт A/B -> Prepare -> предпросмотр -> точная подстройка.
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicMixer.Application.Abstractions;
using MusicMixer.Application.Mashup;
using MusicMixer.Application.YouTube;
using MusicMixer.Desktop.Audition;
using MusicMixer.Desktop.Localization;
using MusicMixer.Desktop.Services;
using MusicMixer.Desktop.Trimming;
using MusicMixer.Desktop.YouTube;
using MusicMixer.Domain.Alignment;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Domain.Models;

namespace MusicMixer.Desktop.ViewModels;

/// <summary>
/// Состояние и команды главного окна. Не содержит DSP и знаний о Demucs/FFmpeg:
/// вся тяжёлая работа уходит в Application/Infrastructure через интерфейсы.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    // Список расширений — только для ранней проверки в UI. Реальную
    // поддержку формата определяет декодер (FFmpeg/BASS).
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".aiff", ".aif", ".wma",
    };

    // Столбиков достаточно для широкой дорожки таймлайна; при ресайзе
    // WaveformView растягивает готовый массив без повторного декодирования.
    private const int WaveformBucketCount = 512;

    // Зависимости приходят из DI (App.axaml.cs). ViewModel намеренно не создаёт
    // их сама, чтобы сценарий можно было тестировать с подставными сервисами.
    private readonly IMashupWorkflow _workflow;
    private readonly IAudioPlayerService _player;
    private readonly IFilePickerService _filePicker;
    private readonly IWorkspaceFactory _workspaceFactory;
    private readonly ILocalizationService _localization;
    private readonly IThemeService _themeService;
    private readonly IYouTubeDialogService _youTubeDialogService;
    private readonly ICoverArtService _coverArtService;
    private readonly HttpClient _httpClient;
    // Отдельная рабочая область для извлечённых обложек.
    private IWorkspace? _coverWorkspace;
    private readonly IAudioTrimmerService _audioTrimmer;
    private readonly IAudioTrimDialogService _audioTrimDialogService;
    private readonly IAudioAuditionDialogService _audioAuditionDialogService;
    // Пики волновой формы для подготовленных стемов.
    private readonly IAudioWaveformService _waveform;
    // Сведение готового микса и выбор файла для сохранения.
    private readonly IMixdownService _mixdownService;
    private readonly IAudioExportDialogService _audioExportDialogService;
    // Таймер обновляет позицию/длительность и ловит окончание трека.
    private readonly DispatcherTimer _playbackTimer;
    // Отменяется при Dispose и связывается со всеми дочерними операциями.
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    // Отдельные источники отмены для трёх независимых операций.
    private CancellationTokenSource? _processCancellation;
    private CancellationTokenSource? _pitchDebounceCancellation;
    private CancellationTokenSource? _trimCancellation;
    private CancellationTokenSource? _exportCancellation;

    // Рабочие области: мэшап, YouTube и обрезка живут независимо, чтобы
    // выбор нового YouTube-трека не удалял уже скачанные файлы.
    private IWorkspace? _workspace;
    private IWorkspace? _youTubeWorkspace;
    private IWorkspace? _trimWorkspace;

    // Текущая подготовленная сессия и план выравнивания. null означает,
    // что предпросмотра ещё нет.
    private MashupSession? _session;
    private AlignmentPlan? _alignment;
    private double _appliedFinePitchSemitones;
    private double _appliedVocalSpeedRatio = 1.0;

    // Ручные поправки анализа, ожидающие рендера. После успешного рендера
    // они совпадают с поправками в _session, при ошибке откатываются к ним.
    private TrackAnalysisOverride _requestedInstrumentalOverride = TrackAnalysisOverride.None;
    private TrackAnalysisOverride _requestedVocalOverride = TrackAnalysisOverride.None;

    // Статусы и ошибки хранятся ключами локализации + аргументами:
    // при смене языка они пересобираются без потери состояния.
    private string _statusKey = "StatusDropTracks";
    private object?[] _statusArguments = [];
    private string? _errorKey;
    private string? _errorDetail;
    private object?[] _errorArguments = [];

    // Флаги подавления: программное изменение слайдера не должно
    // запускать пользовательскую реакцию (повторный рендер/перемотку).
    private bool _suppressFinePitchChange;
    private bool _suppressVocalSpeedChange;
    private bool _suppressOffsetChange;
    private bool _suppressPlaybackProgress;
    private bool _isUserSeeking;
    private bool _disposed;

    public MainViewModel(
        IMashupWorkflow workflow,
        IAudioPlayerService player,
        IFilePickerService filePicker,
        IWorkspaceFactory workspaceFactory,
        ILocalizationService localization,
        IThemeService themeService,
        IYouTubeDialogService youTubeDialogService,
        IAudioTrimmerService audioTrimmer,
        IAudioTrimDialogService audioTrimDialogService,
        IAudioAuditionDialogService audioAuditionDialogService,
        IAudioWaveformService waveform,
        IMixdownService mixdownService,
        IAudioExportDialogService audioExportDialogService,
        ICoverArtService coverArtService,
        HttpClient httpClient)
    {
        _workflow = workflow;
        _player = player;
        _filePicker = filePicker;
        _workspaceFactory = workspaceFactory;
        _localization = localization;
        _themeService = themeService;
        _youTubeDialogService = youTubeDialogService;
        _audioTrimmer = audioTrimmer;
        _audioTrimDialogService = audioTrimDialogService;
        _audioAuditionDialogService = audioAuditionDialogService;
        _waveform = waveform;
        _mixdownService = mixdownService;
        _audioExportDialogService = audioExportDialogService;
        _coverArtService = coverArtService;
        _httpClient = httpClient;

        TrackA = new TrackSlotViewModel(localization);
        TrackB = new TrackSlotViewModel(localization);
        TrackA.PropertyChanged += OnTrackSlotPropertyChanged;
        TrackB.PropertyChanged += OnTrackSlotPropertyChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        _themeService.ThemeChanged += OnThemeChanged;

        // 100 мс — компромисс между плавностью ползунка и нагрузкой на UI.
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += OnPlaybackTimerTick;
        _playbackTimer.Start();

        SetStatus("StatusDropTracks");
        RefreshCommandAvailability();
    }

    public TrackSlotViewModel TrackA { get; }

    public TrackSlotViewModel TrackB { get; }

    [ObservableProperty]
    private bool isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private string? errorMessage;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private bool isProgressIndeterminate;

    [ObservableProperty]
    private bool isPlaying;

    [ObservableProperty]
    private double playbackProgress;

    [ObservableProperty]
    private string playbackPositionText = "0:00";

    [ObservableProperty]
    private string playbackDurationText = "0:00";

    [ObservableProperty]
    private bool hasPreparedMix;

    [ObservableProperty]
    private bool isPlaybackAvailable;

    // Улучшение стемов применяется во время Prepare, поэтому изменение
    // настройки после сборки сбрасывает готовый микс.
    [ObservableProperty]
    private bool isStemEnhancementEnabled = true;

    [ObservableProperty]
    private double finePitchSemitones;

    // Скорость вокала: 1.0 — без изменения, больше — быстрее, меньше — медленнее.
    // После подготовки сюда попадает автоматическое значение, но пользователь
    // может переопределить его ползунком.
    [ObservableProperty]
    private double vocalSpeedRatio = 1.0;

    [ObservableProperty]
    private double offsetNudgeMilliseconds;

    [ObservableProperty]
    private double instrumentalVolume = 0.88;

    [ObservableProperty]
    private double vocalVolume = 0.88;

    // null означает «реальных данных ещё нет»: WaveformView показывает
    // аккуратный макет пустого состояния.
    [ObservableProperty]
    private IReadOnlyList<float>? instrumentalPeaks;

    [ObservableProperty]
    private IReadOnlyList<float>? vocalPeaks;

    public string FinePitchLabel =>
        _localization.Format("FinePitchValue", FinePitchSemitones);

    public string VocalSpeedLabel =>
        _localization.Format("SpeedRatioValue", VocalSpeedRatio);

    public string AutomaticSpeedLabel => _alignment is null
        ? _localization.GetString("NotAvailable")
        : _localization.Format(
            "VocalSpeedAutoValue",
            _alignment.AutomaticPlaybackSpeedRatio);

    public string OffsetNudgeLabel =>
        _localization.Format("OffsetNudgeValue", OffsetNudgeMilliseconds / 1000.0);

    public string SpeedRatioLabel => _alignment is null
        ? _localization.GetString("NotAvailable")
        : _localization.Format(
            "SpeedRatioValue",
            _alignment.Parameters.PlaybackSpeedRatio);

    public string AutomaticPitchLabel => _alignment is null
        ? _localization.GetString("NotAvailable")
        : _localization.Format(
            "AutomaticPitchValue",
            _alignment.AutomaticPitchShiftSemitones);

    public string PitchShiftLabel => _alignment is null
        ? _localization.GetString("NotAvailable")
        : _localization.Format(
            "PitchShiftValue",
            _alignment.Parameters.PitchShiftSemitones);

    public string AutomaticOffsetLabel => _alignment is null
        ? _localization.GetString("NotAvailable")
        : _localization.Format(
            "AutomaticOffsetValue",
            _alignment.AutomaticStartOffset.TotalSeconds);

    public string EffectiveOffsetLabel => _alignment is null
        ? _localization.GetString("NotAvailable")
        : _localization.Format(
            "EffectiveOffsetValue",
            EffectiveOffset.TotalSeconds);

    public string InstrumentalVolumeLabel => $"{Math.Round(InstrumentalVolume * 100)}%";

    public string VocalVolumeLabel => $"{Math.Round(VocalVolume * 100)}%";

    public string TempoPairLabel =>
        TrackA.Track?.BeatsPerMinute is { } instrumentalBpm &&
        TrackB.Track?.BeatsPerMinute is { } vocalBpm
            ? _localization.Format("TempoPairValue", instrumentalBpm, vocalBpm)
            : $"{_localization.GetString("NotAvailable")} / " +
              $"{_localization.GetString("NotAvailable")} BPM";

    public string ProgressPercentText => $"{ProgressPercent:0}%";

    public bool IsNotPlaying => !IsPlaying;

    public bool CanSeekPlayback => !IsBusy && HasPreparedMix && IsPlaybackAvailable;

    public bool CanTuneVocal =>
        !IsBusy && HasPreparedMix && _session is not null && _alignment is not null;

    public string PlayPauseLabel =>
        _localization.GetString(IsPlaying ? "Pause" : "Play");

    public bool IsEnglishSelected =>
        string.Equals(_localization.CurrentCulture, "en-US", StringComparison.Ordinal);

    public bool IsRussianSelected =>
        string.Equals(_localization.CurrentCulture, "ru-RU", StringComparison.Ordinal);

    public bool IsSystemThemeSelected =>
        string.Equals(_themeService.CurrentTheme, ThemeService.SystemTheme, StringComparison.Ordinal);

    public bool IsLightThemeSelected =>
        string.Equals(_themeService.CurrentTheme, ThemeService.LightTheme, StringComparison.Ordinal);

    public bool IsDarkThemeSelected =>
        string.Equals(_themeService.CurrentTheme, ThemeService.DarkTheme, StringComparison.Ordinal);

    private TimeSpan EffectiveOffset => _alignment is null
        ? TimeSpan.FromMilliseconds(OffsetNudgeMilliseconds)
        : _alignment.AutomaticStartOffset + TimeSpan.FromMilliseconds(OffsetNudgeMilliseconds);

    [RelayCommand(CanExecute = nameof(CanProcessTracks))]
    private async Task ProcessTracksAsync()
    {
        // Команда доступна только когда в обоих слотах есть треки.
        if (TrackA.Track is not { } trackA || TrackB.Track is not { } trackB)
        {
            return;
        }

        // Новый запуск отменяет предыдущий. Связанный токен позволяет
        // отменить всё разом при закрытии приложения.
        _processCancellation?.Cancel();
        _processCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _processCancellation = cancellation;

        IsBusy = true;
        ClearError();
        ProgressPercent = 0;
        IsProgressIndeterminate = false;
        SetStatus("StatusPreparing");
        TrackA.SetStatus("StatusQueued");
        TrackB.SetStatus("StatusQueued");

        try
        {
            // Подготовка всегда начинается с чистого листа: старый микс
            // и старые настройки не должны пережить новый Prepare.
            ClearPreparedMix();
            ResetTuningControls();

            var workspace = _workspaceFactory.Create("mashup");
            _workspace = workspace;

            // Progress<T> маршалит обновления в UI-поток.
            var progress = new Progress<OperationProgress>(
                value => UpdateProgress(value, ProgressOperation.Prepare));
            var result = await _workflow.PrepareAsync(
                trackA,
                trackB,
                workspace.DirectoryPath,
                progress,
                cancellation.Token,
                IsStemEnhancementEnabled);

            cancellation.Token.ThrowIfCancellationRequested();

            _session = result.Session;
            _alignment = result.Alignment;
            _requestedInstrumentalOverride = TrackAnalysisOverride.None;
            _requestedVocalOverride = TrackAnalysisOverride.None;
            _appliedFinePitchSemitones = 0;
            _appliedVocalSpeedRatio = result.Alignment.Parameters.PlaybackSpeedRatio;
            SetVocalSpeedSilently(_appliedVocalSpeedRatio);
            TrackA.Track = result.Session.InstrumentalTrack;
            TrackA.SetStatus("StatusInstrumentalReady");
            TrackB.Track = result.Session.VocalTrack;
            TrackB.SetStatus("StatusVocalsReady");

            RefreshAlignmentLabels();
            ProgressPercent = 100;
            SetStatus("StatusLoadingPreview");

            // Волновая форма декодируется параллельно и не отменяет готовую
            // сессию, если FFmpeg не смог построить пики.
            var peaks = await LoadWaveformPeaksAsync(result.Session, cancellation.Token);
            InstrumentalPeaks = peaks.Instrumental;
            VocalPeaks = peaks.Vocal;

            // Даже если BASS недоступен, подготовленные файлы остаются:
            // пользователь видит ошибку воспроизведения, но не теряет результат.
            try
            {
                await LoadPlayerAsync(result.Session, result.Alignment, cancellation.Token);
                IsPlaybackAvailable = true;
                HasPreparedMix = true;
                SetStatus("StatusReady");
            }
            catch (Exception exception) when (
                exception is AudioProcessingException or
                DllNotFoundException or
                BadImageFormatException)
            {
                HasPreparedMix = true;
                IsPlaybackAvailable = false;
                SetErrorFromException(exception);
                SetStatus("StatusMixPreparedNoPlayback");
            }
        }
        catch (OperationCanceledException)
        {
            ClearPreparedMix();
            ResetProgress();
            SetStatus("StatusProcessingCanceled");
            TrackA.SetStatus("StatusReadyToAnalyze");
            TrackB.SetStatus("StatusReadyToAnalyze");
        }
        catch (Exception exception)
        {
            ClearPreparedMix();
            ResetProgress();
            SetErrorFromException(exception);
            SetStatus("StatusPreparationFailed");
            TrackA.SetStatus("StatusAnalysisFailed");
            TrackB.SetStatus("StatusAnalysisFailed");
        }
        finally
        {
            IsProgressIndeterminate = false;
            IsBusy = false;
            if (ReferenceEquals(_processCancellation, cancellation))
            {
                _processCancellation = null;
            }

            cancellation.Dispose();
            RefreshCommandAvailability();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPickFiles))]
    private Task PickTrackAAsync() => PickTrackAsync(isTrackA: true);

    [RelayCommand(CanExecute = nameof(CanPickFiles))]
    private Task PickTrackBAsync() => PickTrackAsync(isTrackA: false);

    /// <summary>
    /// Меняет инструментал и вокал местами. Готовый микс после этого
    /// недействителен: роли дорожек изменились, поэтому его нужно собрать заново.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSwapTracks))]
    private void SwapTracks()
    {
        if (IsBusy)
        {
            return;
        }

        ClearPreparedMix();
        ResetTuningControls();
        TrackA.SwapWith(TrackB);
        ClearError();
        RefreshTrackLabels();
        SetStatus(TrackA.HasTrack && TrackB.HasTrack
            ? "StatusTracksSwapped"
            : "StatusTrackMoved");
        RefreshCommandAvailability();
    }

    /// <summary>
    /// Возвращает автоматическую скорость, рассчитанную по BPM обоих треков.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTuneVocal))]
    private void ResetVocalSpeed()
    {
        if (_alignment is null)
        {
            return;
        }

        VocalSpeedRatio = _alignment.AutomaticPlaybackSpeedRatio;
    }

    /// <summary>
    /// Убирает изменение скорости: вокал звучит в своём исходном темпе.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTuneVocal))]
    private void SetNeutralVocalSpeed()
    {
        VocalSpeedRatio = 1.0;
    }

    [RelayCommand]
    private void ToggleStemEnhancement()
    {
        if (!IsBusy)
        {
            IsStemEnhancementEnabled = !IsStemEnhancementEnabled;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPickFiles))]
    private async Task OpenYouTubeSearchAsync()
    {
        try
        {
            // Пока открыт диалог, предпросмотр лучше поставить на паузу:
            // скачивание и конвертация не должны звучать поверх него.
            if (IsPlaying)
            {
                _player.Pause();
                IsPlaying = false;
                SetStatus("StatusPaused");
            }

            // Отдельная рабочая область YouTube: переживает смену мэшапа.
            var workspace = _youTubeWorkspace ??= _workspaceFactory.Create("youtube");
            var result = await _youTubeDialogService.ShowAsync(
                workspace.DirectoryPath,
                _lifetimeCancellation.Token);

            if (result is null)
            {
                return;
            }

            if (!File.Exists(result.AudioFilePath))
            {
                throw new FileNotFoundException(
                    "The YouTube audio download was not found.",
                    result.AudioFilePath);
            }

            var isInstrumental = result.Target == TrackTarget.Instrumental;
            var track = new AudioTrack(
                result.AudioFilePath,
                new TrackMetadata(result.Track.Title, result.Track.Author),
                result.Track.Duration ?? TimeSpan.Zero);

            var confirmed = await ConfirmAuditionAsync(
                result.AudioFilePath,
                result.Track.Title,
                $"{result.Track.Author} · " +
                _localization.GetString(
                    isInstrumental ? "RoleInstrumental" : "RoleVocals"),
                _lifetimeCancellation.Token);
            if (!confirmed)
            {
                return;
            }

            ApplySelectedTrack(isInstrumental, track, result.Track.ThumbnailUrl);

            ClearError();
            SetStatus("StatusTrackAssigned");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetErrorFromException(exception);
        }
    }

    [RelayCommand(CanExecute = nameof(CanTrimTrackA))]
    private Task TrimTrackAAsync() => TrimTrackAsync(isTrackA: true);

    [RelayCommand(CanExecute = nameof(CanTrimTrackB))]
    private Task TrimTrackBAsync() => TrimTrackAsync(isTrackA: false);

    [RelayCommand(CanExecute = nameof(CanPlayPause))]
    private void PlayPause()
    {
        if (!_player.IsLoaded)
        {
            return;
        }

        try
        {
            // Одна кнопка переключает состояние; подпись берётся из PlayPauseLabel.
            if (IsPlaying)
            {
                _player.Pause();
                IsPlaying = false;
                SetStatus("StatusPaused");
                return;
            }

            _player.Play();
            IsPlaying = true;
            SetStatus("StatusPlaying");
        }
        catch (Exception exception)
        {
            IsPlaying = false;
            SetErrorFromException(exception);
            SetStatus("StatusPlaybackFailed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopPlayback))]
    private void Stop()
    {
        try
        {
            _player.Stop();
            IsPlaying = false;
            SetPlaybackProgressSilently(0);
            PlaybackPositionText = FormatTime(TimeSpan.Zero);
            SetStatus("StatusStopped");
        }
        catch (Exception exception)
        {
            SetErrorFromException(exception);
        }
    }

    /// <summary>
    /// Сохраняет готовый микс в WAV. Пользователь выбирает путь, после чего
    /// FFmpeg сводит инструментал и выровненный вокал с текущими громкостями
    /// и смещением — результат совпадает с предпросмотром.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportMix))]
    private async Task ExportMixAsync()
    {
        if (_session is not { } session || _alignment is null || IsBusy)
        {
            return;
        }

        string? outputPath;
        try
        {
            outputPath = await _audioExportDialogService.PickOutputPathAsync(
                BuildExportFileName(session),
                _localization.GetString("ExportDialogTitle"),
                _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            SetErrorFromException(exception);
            return;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        outputPath = EnsureWavExtension(outputPath);

        // Экспорт читает те же файлы, что и плеер. Пауза освобождает тракт
        // и не даёт пользователю слушать трек, который уже сохраняется.
        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
        }

        // Отложенный рендер вокала не должен вклиниться в сохранение.
        _pitchDebounceCancellation?.Cancel();
        _exportCancellation?.Cancel();
        _exportCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _exportCancellation = cancellation;

        IsBusy = true;
        ClearError();
        ProgressPercent = 0;
        IsProgressIndeterminate = false;
        SetStatus("StatusExporting");

        try
        {
            var progress = new Progress<OperationProgress>(
                value => UpdateProgress(value, ProgressOperation.Export));

            await RenderMixAsync(session, outputPath, progress, cancellation.Token);

            ProgressPercent = 100;
            SetStatus("StatusExportComplete", Path.GetFileName(outputPath));
        }
        catch (OperationCanceledException)
        {
            ResetProgress();
            SetStatus("StatusExportCanceled");
        }
        catch (AudioProcessingException exception)
        {
            ResetProgress();
            SetError("ErrorExportFailed", exception.Diagnostics ?? exception.Message);
            SetStatus("StatusExportFailed");
        }
        catch (Exception exception)
        {
            ResetProgress();
            SetErrorFromException(exception);
            SetStatus("StatusExportFailed");
        }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(_exportCancellation, cancellation))
            {
                _exportCancellation = null;
            }

            cancellation.Dispose();
            RefreshCommandAvailability();
        }
    }

    /// <summary>
    /// Сначала сводит полный микс во временный WAV, даёт выбрать фрагмент
    /// в диалоге обрезки и только потом сохраняет выбранный диапазон.
    /// Так пользователь обрезает уже готовую звуковую дорожку, а не источник.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportMix))]
    private async Task TrimAndExportMixAsync()
    {
        if (_session is not { } session || _alignment is null || IsBusy)
        {
            return;
        }

        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
        }

        // Отложенный рендер вокала не должен вклиниться в подготовку микса.
        _pitchDebounceCancellation?.Cancel();
        _exportCancellation?.Cancel();
        _exportCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _exportCancellation = cancellation;

        IsBusy = true;
        ClearError();
        ProgressPercent = 0;
        IsProgressIndeterminate = false;
        SetStatus("StatusRenderingMixForTrim");

        try
        {
            // Временный файл живёт только на время диалога и удаляется
            // вместе с рабочей областью, даже если пользователь отменит выбор.
            using var workspace = _workspaceFactory.Create("export");
            var mixPath = workspace.GetPath($"mix-{Guid.NewGuid():N}.wav");
            var renderProgress = new Progress<OperationProgress>(
                value => ProgressPercent = Math.Clamp(value.Fraction, 0, 1) * 50);

            await RenderMixAsync(session, mixPath, renderProgress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            ProgressPercent = 50;

            var selection = await _audioTrimDialogService.ShowAsync(
                new AudioTrimRequest(
                    mixPath,
                    _localization.GetString("TrimMixTitle"),
                    _localization.GetString("TrimMixRole")),
                cancellation.Token);

            if (selection is null)
            {
                ResetProgress();
                SetStatus("StatusExportCanceled");
                return;
            }

            var outputPath = await _audioExportDialogService.PickOutputPathAsync(
                BuildTrimmedExportFileName(session),
                _localization.GetString("ExportTrimmedDialogTitle"),
                cancellation.Token);

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                ResetProgress();
                SetStatus("StatusExportCanceled");
                return;
            }

            outputPath = EnsureWavExtension(outputPath);
            SetStatus("StatusTrimmingMix");

            var trimProgress = new Progress<OperationProgress>(
                value => ProgressPercent =
                    50 + (Math.Clamp(value.Fraction, 0, 1) * 50));

            await _audioTrimmer.TrimAsync(
                mixPath,
                outputPath,
                selection.Start,
                selection.End,
                trimProgress,
                cancellation.Token);

            ProgressPercent = 100;
            SetStatus("StatusExportComplete", Path.GetFileName(outputPath));
        }
        catch (OperationCanceledException)
        {
            ResetProgress();
            SetStatus("StatusExportCanceled");
        }
        catch (AudioProcessingException exception)
        {
            ResetProgress();
            SetError("ErrorExportFailed", exception.Diagnostics ?? exception.Message);
            SetStatus("StatusExportFailed");
        }
        catch (Exception exception)
        {
            ResetProgress();
            SetErrorFromException(exception);
            SetStatus("StatusExportFailed");
        }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(_exportCancellation, cancellation))
            {
                _exportCancellation = null;
            }

            cancellation.Dispose();
            RefreshCommandAvailability();
        }
    }

    public void BeginPlaybackSeek()
    {
        // Пока пользователь тянет ползунок, таймер не должен перезаписывать
        // его позицию значением из плеера.
        if (CanSeekPlayback)
        {
            _isUserSeeking = true;
        }
    }

    public void EndPlaybackSeek()
    {
        if (!_isUserSeeking)
        {
            return;
        }

        _isUserSeeking = false;
        // Перемотка выполняется один раз, когда пользователь отпустил ползунок.
        SeekToPlaybackProgress(PlaybackProgress);
    }

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void CancelProcessing()
    {
        // Отменяем все виды работ: Prepare, отложенный рендер тона и обрезку.
        _processCancellation?.Cancel();
        _pitchDebounceCancellation?.Cancel();
        _trimCancellation?.Cancel();
        _exportCancellation?.Cancel();
        SetStatus("StatusCanceling");
    }

    [RelayCommand]
    private void DismissError()
    {
        _errorKey = null;
        _errorDetail = null;
        _errorArguments = [];
        ErrorMessage = null;
    }

    [RelayCommand]
    private void SetLanguage(string cultureCode)
    {
        try
        {
            _localization.SetLanguage(cultureCode);
        }
        catch (ArgumentException)
        {
        }
    }

    [RelayCommand]
    private void SetTheme(string theme)
    {
        try
        {
            _themeService.SetTheme(theme);
        }
        catch (ArgumentException)
        {
        }
    }

    public async Task SetTrackAsync(bool isTrackA, string filePath)
    {
        if (IsBusy)
        {
            SetError("ErrorWaitForOperation", null);
            return;
        }

        try
        {
            // Порядок важен: сначала проверяем файл, затем даём прослушать,
            // и только после подтверждения меняем слот.
            var fullPath = ValidateAudioFile(filePath);
            var track = AudioTrack.CreateUnanalyzed(fullPath);
            var confirmed = await ConfirmAuditionAsync(
                fullPath,
                track.Metadata.Title,
                _localization.GetString(isTrackA ? "RoleInstrumental" : "RoleVocals"),
                _lifetimeCancellation.Token);

            if (!confirmed)
            {
                return;
            }

            ApplySelectedTrack(isTrackA, track);
            ClearError();
            SetStatus(TrackA.HasTrack && TrackB.HasTrack
                ? "StatusBothLoaded"
                : "StatusAddOther");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetErrorFromException(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localization.LanguageChanged -= OnLanguageChanged;
        _themeService.ThemeChanged -= OnThemeChanged;
        TrackA.PropertyChanged -= OnTrackSlotPropertyChanged;
        TrackB.PropertyChanged -= OnTrackSlotPropertyChanged;
        TrackA.Dispose();
        TrackB.Dispose();

        _lifetimeCancellation.Cancel();
        _processCancellation?.Cancel();
        _pitchDebounceCancellation?.Cancel();
        _trimCancellation?.Cancel();
        _exportCancellation?.Cancel();
        _playbackTimer.Stop();
        _playbackTimer.Tick -= OnPlaybackTimerTick;

        try
        {
            _player.Stop();
        }
        catch (Exception)
        {
        }

        try
        {
            _player.Unload();
        }
        catch (Exception)
        {
        }

        _workspace?.Dispose();
        _workspace = null;
        _youTubeWorkspace?.Dispose();
        _youTubeWorkspace = null;
        _trimWorkspace?.Dispose();
        _trimWorkspace = null;
        _coverWorkspace?.Dispose();
        _coverWorkspace = null;
        _processCancellation?.Dispose();
        _pitchDebounceCancellation?.Dispose();
        _trimCancellation?.Dispose();
        _exportCancellation?.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private void ApplySelectedTrack(bool isTrackA, AudioTrack track, string? thumbnailUrl = null)
    {
        // Любой новый исходник делает старый микс недействительным:
        // его нельзя показывать как готовый к воспроизведению.
        ClearPreparedMix();
        ResetTuningControls();

        var slot = isTrackA ? TrackA : TrackB;
        slot.SetImportedTrack(track, track.FilePath);
        slot.SetStatus("StatusReadyToAnalyze");
        // Обложка грузится в фоне и не задерживает выбор трека.
        _ = LoadCoverAsync(slot, slot.CoverToken, track.FilePath, thumbnailUrl);

        RefreshTrackLabels();
        RefreshCommandAvailability();
    }

    /// <summary>
    /// Загружает обложку для карточки: превью YouTube по ссылке или встроенную
    /// в файл картинку через ffmpeg. Ошибки не показываются: без обложки
    /// карточка просто остаётся с названием.
    /// </summary>
    private async Task LoadCoverAsync(
        TrackSlotViewModel slot,
        int token,
        string audioFilePath,
        string? thumbnailUrl)
    {
        try
        {
            var cancellationToken = _lifetimeCancellation.Token;
            byte[]? imageBytes = null;
            if (!string.IsNullOrWhiteSpace(thumbnailUrl))
            {
                imageBytes = await _httpClient.GetByteArrayAsync(thumbnailUrl, cancellationToken);
            }
            else
            {
                var workspace = _coverWorkspace ??= _workspaceFactory.Create("covers");
                var outputPath = Path.Combine(
                    workspace.DirectoryPath,
                    Guid.NewGuid().ToString("N") + ".png");
                var coverPath = await _coverArtService.ExtractEmbeddedCoverAsync(
                    audioFilePath,
                    outputPath,
                    cancellationToken);
                if (coverPath is not null)
                {
                    imageBytes = await File.ReadAllBytesAsync(coverPath, cancellationToken);
                }
            }

            if (imageBytes is null || imageBytes.Length == 0 || _disposed)
            {
                return;
            }

            using var stream = new MemoryStream(imageBytes);
            var bitmap = Bitmap.DecodeToWidth(stream, 256);
            slot.TrySetCover(token, bitmap);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Обложка необязательна: битый файл или нет сети не должны мешать работе.
        }
    }

    private async Task TrimTrackAsync(bool isTrackA)
    {
        var slot = isTrackA ? TrackA : TrackB;
        if (slot.Track is not { } track || IsBusy)
        {
            return;
        }

        try
        {
            // Обрезка всегда открывается по исходному файлу, а не по
            // предыдущему обрезанному результату.
            if (IsPlaying)
            {
                _player.Pause();
                IsPlaying = false;
                SetStatus("StatusPaused");
            }

            var sourcePath = slot.OriginalFilePath ?? track.FilePath;
            var request = new AudioTrimRequest(
                sourcePath,
                track.Metadata.Title,
                _localization.GetString(isTrackA ? "RoleInstrumental" : "RoleVocals"),
                slot.TrimStart,
                slot.TrimEnd);

            var selection = await _audioTrimDialogService.ShowAsync(
                request,
                _lifetimeCancellation.Token);

            if (selection is null)
            {
                return;
            }

            _trimCancellation?.Cancel();
            _trimCancellation?.Dispose();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            _trimCancellation = cancellation;

            IsBusy = true;
            ClearError();
            ProgressPercent = 0;
            IsProgressIndeterminate = false;
            SetStatus("StatusTrimming");

            try
            {
                // Отдельная рабочая область для обрезок: несколько прогонов
                // не удаляют файлы друг друга.
                var workspace = _trimWorkspace ??= _workspaceFactory.Create("trims");
                var outputPath = workspace.GetPath($"trim-{Guid.NewGuid():N}.wav");
                var progress = new Progress<OperationProgress>(
                    value => ProgressPercent = value.Fraction * 100);

                var trimmedPath = await _audioTrimmer.TrimAsync(
                    sourcePath,
                    outputPath,
                    selection.Start,
                    selection.End,
                    progress,
                    cancellation.Token);

                cancellation.Token.ThrowIfCancellationRequested();

                var trimmedTrack = new AudioTrack(
                    trimmedPath,
                    track.Metadata,
                    selection.Duration);

                ClearPreparedMix();
                ResetTuningControls();
                slot.ApplyTrim(
                    sourcePath,
                    trimmedTrack,
                    selection.Start,
                    selection.End);
                slot.SetStatus("StatusReadyToAnalyze");
                ClearError();
                ProgressPercent = 100;
                SetStatus("StatusTrimApplied");
                RefreshTrackLabels();
            }
            catch (OperationCanceledException)
            {
                ResetProgress();
                SetStatus("StatusProcessingCanceled");
            }
            catch (Exception exception)
            {
                ResetProgress();
                SetErrorFromException(exception);
                SetStatus("StatusTrimFailed");
            }
            finally
            {
                IsBusy = false;
                if (ReferenceEquals(_trimCancellation, cancellation))
                {
                    _trimCancellation = null;
                }

                cancellation.Dispose();
                RefreshCommandAvailability();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetErrorFromException(exception);
            SetStatus("StatusTrimFailed");
        }
    }

    private async Task PickTrackAsync(bool isTrackA)
    {
        try
        {
            var path = await _filePicker.PickAudioFileAsync(
                _localization.GetString(
                    isTrackA ? "TrackA" : "TrackB"),
                _lifetimeCancellation.Token);

            if (!string.IsNullOrWhiteSpace(path))
            {
                await SetTrackAsync(isTrackA, path);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError("ErrorPicker", exception.Message);
        }
    }

    private async Task<bool> ConfirmAuditionAsync(
        string filePath,
        string title,
        string subtitle,
        CancellationToken cancellationToken)
    {
        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
            SetStatus("StatusPaused");
        }

        return await _audioAuditionDialogService.ShowAsync(
            new AudioAuditionRequest(filePath, title, subtitle),
            cancellationToken);
    }

    private async Task LoadPlayerAsync(
        MashupSession session,
        AlignmentPlan alignment,
        CancellationToken cancellationToken)
    {
        // Плеер получает уже готовые файлы и параметры, но сам ничего
        // не рендерит: предпросмотр должен совпадать с результатом.
        await _player.LoadAsync(
            session.InstrumentalPath,
            session.AlignedVocalPath,
            alignment.Parameters,
            cancellationToken);

        _player.SetVolumes(InstrumentalVolume, VocalVolume);
        _player.SetStartOffset(EffectiveOffset);
        IsPlaying = false;
        SetPlaybackProgressSilently(0);
        PlaybackPositionText = FormatTime(TimeSpan.Zero);
        PlaybackDurationText = FormatTime(_player.Duration);
    }

    /// <summary>
    /// Сводит текущую сессию в один WAV с учётом смещения и громкостей.
    /// Используется и обычным сохранением, и сохранением после обрезки.
    /// </summary>
    private Task RenderMixAsync(
        MashupSession session,
        string outputPath,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var duration = _player.IsLoaded && _player.Duration > TimeSpan.Zero
            ? _player.Duration
            : session.InstrumentalTrack.Duration;

        return _mixdownService.ExportAsync(
            session.InstrumentalPath,
            session.AlignedVocalPath,
            EffectiveOffset,
            InstrumentalVolume,
            VocalVolume,
            duration,
            outputPath,
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Строит пики для обоих стемов параллельно. Волновая форма — украшение,
    /// а не часть конвейера, поэтому её сбой не должен проваливать подготовку.
    /// </summary>
    private async Task<(IReadOnlyList<float>? Instrumental, IReadOnlyList<float>? Vocal)>
        LoadWaveformPeaksAsync(MashupSession session, CancellationToken cancellationToken)
    {
        try
        {
            var instrumentalTask = _waveform.ExtractPeaksAsync(
                session.InstrumentalPath,
                WaveformBucketCount,
                cancellationToken);
            var vocalTask = _waveform.ExtractPeaksAsync(
                session.AlignedVocalPath,
                WaveformBucketCount,
                cancellationToken);

            await Task.WhenAll(instrumentalTask, vocalTask);
            return (await instrumentalTask, await vocalTask);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// После повторного рендера вокала его длительность может измениться.
    /// Пики обновляются без права отменить уже готовый и загруженный микс.
    /// </summary>
    private async Task RefreshVocalWaveformAsync(
        string alignedVocalPath,
        CancellationToken cancellationToken)
    {
        try
        {
            VocalPeaks = await _waveform.ExtractPeaksAsync(
                alignedVocalPath,
                WaveformBucketCount,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            VocalPeaks = null;
        }
    }

    private void ScheduleVocalRender()
    {
        if (_session is null || _alignment is null || _workspace is null || IsBusy || _disposed)
        {
            return;
        }

        // Каждое движение ползунков скорости/тона отменяет предыдущее ожидание
        // и запускает новое. Рендер начнётся только после 350 мс тишины.
        var previous = _pitchDebounceCancellation;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _pitchDebounceCancellation = cancellation;
        previous?.Cancel();

        _ = DebounceVocalRenderAsync(cancellation);
    }

    private async Task DebounceVocalRenderAsync(CancellationTokenSource cancellation)
    {
        try
        {
            // Задержка склеивает серию быстрых изменений слайдера в один рендер.
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellation.Token);
            await ApplyVocalTuningAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                SetErrorFromException(exception);
                SetStatus("StatusFineTuneFailed");
            }
        }
        finally
        {
            if (ReferenceEquals(_pitchDebounceCancellation, cancellation))
            {
                _pitchDebounceCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task ApplyVocalTuningAsync(CancellationToken cancellationToken)
    {
        if (_session is not { } session ||
            _alignment is not { } currentAlignment ||
            _workspace is not { } workspace)
        {
            return;
        }

        // Запоминаем состояние до рендера, чтобы вернуть пользователя
        // примерно туда же, где он слушал трек.
        // Ручные поправки анализа применяются к сессии только вместе с рендером:
        // до успешного результата в _session остаются прежние значения.
        var requestedSession = session.WithAnalysisOverrides(
            _requestedInstrumentalOverride,
            _requestedVocalOverride);
        var analysisChanged =
            !Equals(requestedSession.InstrumentalOverride, session.InstrumentalOverride) ||
            !Equals(requestedSession.VocalOverride, session.VocalOverride);
        var requestedFinePitch = FinePitchSemitones;
        var requestedSpeedRatio = VocalSpeedRatio;
        var previousFinePitch = _appliedFinePitchSemitones;
        var previousSpeedRatio = _appliedVocalSpeedRatio;
        var wasPlaying = IsPlaying;
        var previousPosition = _player.IsLoaded ? _player.Position : TimeSpan.Zero;
        var playerReloaded = false;

        IsBusy = true;
        ClearError();
        IsProgressIndeterminate = false;
        ProgressPercent = 0;
        SetStatus("StatusAligningVocals");

        try
        {
            if (wasPlaying)
            {
                _player.Pause();
                IsPlaying = false;
            }

            var outputPath = workspace.GetPath($"aligned-vocals-{Guid.NewGuid():N}.wav");
            var progress = new Progress<OperationProgress>(
                value => UpdateProgress(value, ProgressOperation.FineTune));
            var render = await _workflow.RenderAlignedVocalAsync(
                requestedSession,
                requestedFinePitch,
                TimeSpan.FromMilliseconds(OffsetNudgeMilliseconds),
                outputPath,
                progress,
                cancellationToken,
                requestedSpeedRatio);

            cancellationToken.ThrowIfCancellationRequested();

            var updatedSession = requestedSession.WithAlignedVocal(render.AlignedVocalPath);
            await _player.LoadAsync(
                updatedSession.InstrumentalPath,
                updatedSession.AlignedVocalPath,
                render.Alignment.Parameters,
                cancellationToken);

            playerReloaded = true;
            _session = updatedSession;
            _alignment = render.Alignment;
            _appliedFinePitchSemitones = requestedFinePitch;
            _appliedVocalSpeedRatio = requestedSpeedRatio;
            HasPreparedMix = true;
            if (analysisChanged)
            {
                TrackA.SetAnalysis(updatedSession.InstrumentalTrack, updatedSession.InstrumentalOverride);
                TrackB.SetAnalysis(updatedSession.VocalTrack, updatedSession.VocalOverride);
            }
            IsPlaybackAvailable = true;

            _player.SetVolumes(InstrumentalVolume, VocalVolume);
            _player.SetStartOffset(EffectiveOffset);
            var seekPosition = previousPosition < _player.Duration
                ? previousPosition
                : TimeSpan.Zero;
            _player.Seek(seekPosition);

            await RefreshVocalWaveformAsync(
                updatedSession.AlignedVocalPath,
                cancellationToken);

            SetPlaybackProgressSilently(
                _player.Duration.TotalSeconds > 0
                    ? Math.Clamp(seekPosition.TotalSeconds / _player.Duration.TotalSeconds, 0, 1)
                    : 0);
            PlaybackPositionText = FormatTime(seekPosition);
            PlaybackDurationText = FormatTime(_player.Duration);
            ProgressPercent = 100;
            RefreshAlignmentLabels();
            SetStatus(analysisChanged ? "StatusAnalysisOverrideApplied" : "StatusFineTuneApplied");

            if (wasPlaying)
            {
                _player.Play();
                IsPlaying = true;
            }
        }
        catch (OperationCanceledException)
        {
            if (!playerReloaded)
            {
                SetFinePitchSilently(previousFinePitch);
                SetVocalSpeedSilently(previousSpeedRatio);
                TryResumeAfterFailedFineTune(wasPlaying);
            }

            ResetProgress();
            SetStatus("StatusFineTuneCanceled");
            RefreshAlignmentLabels();
        }
        catch (Exception exception)
        {
            if (!playerReloaded)
            {
                SetFinePitchSilently(previousFinePitch);
                SetVocalSpeedSilently(previousSpeedRatio);
                TryResumeAfterFailedFineTune(wasPlaying);
                _alignment = currentAlignment;
            }

            ResetProgress();
            SetErrorFromException(exception);
            SetStatus("StatusFineTuneFailed");
            RefreshAlignmentLabels();
        }
        finally
        {
            // Если рендер не удался, ожидающие поправки возвращаются к тем,
            // что реально звучат в сессии; после успеха это ничего не меняет.
            if (_session is { } appliedSession)
            {
                _requestedInstrumentalOverride = appliedSession.InstrumentalOverride;
                _requestedVocalOverride = appliedSession.VocalOverride;
            }

            IsBusy = false;
            IsProgressIndeterminate = false;
            RefreshCommandAvailability();
        }
    }

    /// <summary>
    /// «Применить» в редакторе анализа: берёт BPM, первую долю и тональность
    /// из полей карточки и пересобирает вокал с новым выравниванием.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTuneVocal))]
    private void ApplyAnalysisOverride(string? slotId)
    {
        var isTrackA = IsTrackASlot(slotId);
        var slot = isTrackA ? TrackA : TrackB;
        RequestAnalysisOverride(isTrackA, slot.BuildAnalysisOverride());
    }

    /// <summary>«Авто»: убирает ручные поправки дорожки и возвращает анализ.</summary>
    [RelayCommand(CanExecute = nameof(CanTuneVocal))]
    private void ResetAnalysisOverride(string? slotId) =>
        RequestAnalysisOverride(IsTrackASlot(slotId), TrackAnalysisOverride.None);

    private void RequestAnalysisOverride(bool isTrackA, TrackAnalysisOverride analysisOverride)
    {
        if (_session is not { } session || _alignment is not { } alignment)
        {
            return;
        }

        var instrumentalOverride = isTrackA ? analysisOverride : _requestedInstrumentalOverride;
        var vocalOverride = isTrackA ? _requestedVocalOverride : analysisOverride;
        if (Equals(instrumentalOverride, _requestedInstrumentalOverride) &&
            Equals(vocalOverride, _requestedVocalOverride))
        {
            // Значения не изменились: возвращаем поля редактора к применённым
            // значениям, повторный рендер не нужен.
            var slot = isTrackA ? TrackA : TrackB;
            slot.SetAnalysis(
                isTrackA ? session.InstrumentalTrack : session.VocalTrack,
                isTrackA ? session.InstrumentalOverride : session.VocalOverride);
            return;
        }

        var candidate = session.WithAnalysisOverrides(instrumentalOverride, vocalOverride);
        AlignmentPlan preview;
        try
        {
            // Предварительный расчёт без рендера: проверяем, что с новыми
            // значениями треки можно выровнять, и узнаём новый автотемп.
            preview = AlignmentCalculator.Calculate(
                candidate.EffectiveInstrumentalTrack,
                candidate.EffectiveVocalTrack,
                FinePitchSemitones,
                TimeSpan.FromMilliseconds(OffsetNudgeMilliseconds));
        }
        catch (AlignmentException exception)
        {
            SetErrorFromException(exception);
            SetStatus("StatusFineTuneFailed");
            return;
        }

        ClearError();
        _requestedInstrumentalOverride = instrumentalOverride;
        _requestedVocalOverride = vocalOverride;

        // Новый BPM меняет автоматический коэффициент темпа. Ползунок скорости
        // переставляем на него, иначе прежняя скорость перекрыла бы поправку.
        if (Math.Abs(preview.AutomaticPlaybackSpeedRatio - alignment.AutomaticPlaybackSpeedRatio) > 1e-9)
        {
            SetVocalSpeedSilently(preview.AutomaticPlaybackSpeedRatio);
        }

        ScheduleVocalRender();
    }

    private static bool IsTrackASlot(string? slotId) =>
        !string.Equals(slotId, "B", StringComparison.OrdinalIgnoreCase);

    private void TryResumeAfterFailedFineTune(bool wasPlaying)
    {
        if (!wasPlaying || !_player.IsLoaded)
        {
            return;
        }

        try
        {
            _player.Play();
            IsPlaying = true;
        }
        catch (Exception exception)
        {
            SetErrorFromException(exception);
            SetStatus("StatusPlaybackFailed");
        }
    }

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (_disposed || !_player.IsLoaded)
        {
            return;
        }

        var duration = _player.Duration;
        var position = _player.Position;
        var playing = _player.IsPlaying;

        PlaybackDurationText = FormatTime(duration);
        // Во время перетаскивания ползунка позицию задаёт пользователь,
        // поэтому таймер её не перезаписывает.
        if (!_isUserSeeking)
        {
            PlaybackPositionText = FormatTime(position);
            SetPlaybackProgressSilently(
                duration.TotalSeconds > 0
                    ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1)
                    : 0);
        }

        if (IsPlaying == playing)
        {
            return;
        }

        IsPlaying = playing;
        if (!playing && HasPreparedMix && PlaybackProgress >= 0.995)
        {
            SetStatus("StatusPlaybackFinished");
        }
    }

    private void UpdateProgress(OperationProgress progress, ProgressOperation operation)
    {
        ProgressPercent = progress.Fraction * 100;

        // Диапазоны должны совпадать с MashupWorkflow: 0–5 % анализ A,
        // 5–10 % анализ B, 10–38 % разделение A, 38–66 % разделение B,
        // 66–75 % улучшение инструментала, 75–84 % улучшение вокала,
        // 84–99 % рендер вокала. Иначе подпись этапа разойдётся с прогрессом.
        var key = operation switch
        {
            ProgressOperation.FineTune => "StatusAligningVocals",
            ProgressOperation.Export => "StatusExporting",
            _ when progress.Fraction < 0.05 => "StatusAnalyzingTrackA",
            _ when progress.Fraction < 0.10 => "StatusAnalyzingTrackB",
            _ when progress.Fraction < 0.38 => "StatusSeparatingTrackA",
            _ when progress.Fraction < 0.66 => "StatusSeparatingTrackB",
            _ when progress.Fraction < 0.75 => "StatusEnhancingInstrumental",
            _ when progress.Fraction < 0.84 => "StatusEnhancingVocal",
            _ when progress.Fraction < 0.99 => "StatusAligningVocals",
            _ => "StatusLoadingPreview",
        };

        SetStatus(key);
    }

    private void ClearPreparedMix()
    {
        // Отменяем отложенный рендер и освобождаем и файлы, и нативные
        // дескрипторы плеера. Ошибки здесь намеренно не пробрасываются:
        // очистка не должна мешать следующему сценарию.
        _pitchDebounceCancellation?.Cancel();

        try
        {
            _player.Stop();
        }
        catch (Exception)
        {
        }

        try
        {
            _player.Unload();
        }
        catch (Exception)
        {
        }

        _session = null;
        _alignment = null;
        _requestedInstrumentalOverride = TrackAnalysisOverride.None;
        _requestedVocalOverride = TrackAnalysisOverride.None;
        _appliedFinePitchSemitones = 0;
        _appliedVocalSpeedRatio = 1.0;
        SetVocalSpeedSilently(1.0);
        _workspace?.Dispose();
        _workspace = null;

        IsPlaying = false;
        HasPreparedMix = false;
        IsPlaybackAvailable = false;
        InstrumentalPeaks = null;
        VocalPeaks = null;
        _isUserSeeking = false;
        SetPlaybackProgressSilently(0);
        PlaybackPositionText = FormatTime(TimeSpan.Zero);
        PlaybackDurationText = FormatTime(TimeSpan.Zero);
        RefreshAlignmentLabels();
        RefreshCommandAvailability();
    }

    private void ResetTuningControls()
    {
        _suppressFinePitchChange = true;
        FinePitchSemitones = 0;
        _suppressFinePitchChange = false;

        SetVocalSpeedSilently(1.0);
        _appliedVocalSpeedRatio = 1.0;

        _suppressOffsetChange = true;
        OffsetNudgeMilliseconds = 0;
        _suppressOffsetChange = false;

        OnPropertyChanged(nameof(FinePitchLabel));
        OnPropertyChanged(nameof(VocalSpeedLabel));
        OnPropertyChanged(nameof(AutomaticSpeedLabel));
        OnPropertyChanged(nameof(OffsetNudgeLabel));
        OnPropertyChanged(nameof(EffectiveOffsetLabel));
    }

    private void ResetProgress()
    {
        ProgressPercent = 0;
        IsProgressIndeterminate = false;
    }

    private void SetPlaybackProgressSilently(double value)
    {
        _suppressPlaybackProgress = true;
        try
        {
            PlaybackProgress = Math.Clamp(value, 0, 1);
        }
        finally
        {
            _suppressPlaybackProgress = false;
        }
    }

    private void SeekToPlaybackProgress(double value)
    {
        if (_disposed || !CanSeekPlayback)
        {
            return;
        }

        var duration = _player.Duration;
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            var normalized = Math.Clamp(value, 0, 1);
            var target = TimeSpan.FromSeconds(normalized * duration.TotalSeconds);
            _player.Seek(target);
            SetPlaybackProgressSilently(normalized);
            PlaybackPositionText = FormatTime(target);
        }
        catch (Exception exception)
        {
            SetErrorFromException(exception);
            SetStatus("StatusPlaybackFailed");
        }
    }

    private void SetFinePitchSilently(double value)
    {
        _suppressFinePitchChange = true;
        FinePitchSemitones = value;
        _suppressFinePitchChange = false;
        OnPropertyChanged(nameof(FinePitchLabel));
    }

    private void SetVocalSpeedSilently(double value)
    {
        _suppressVocalSpeedChange = true;
        VocalSpeedRatio = Math.Clamp(
            value,
            AlignmentParameters.MinimumPlaybackSpeedRatio,
            AlignmentParameters.MaximumPlaybackSpeedRatio);
        _suppressVocalSpeedChange = false;
        OnPropertyChanged(nameof(VocalSpeedLabel));
    }

    private void RefreshTrackLabels()
    {
        OnPropertyChanged(nameof(TempoPairLabel));
        RefreshAlignmentLabels();
    }

    private void RefreshAlignmentLabels()
    {
        OnPropertyChanged(nameof(SpeedRatioLabel));
        OnPropertyChanged(nameof(VocalSpeedLabel));
        OnPropertyChanged(nameof(AutomaticSpeedLabel));
        OnPropertyChanged(nameof(AutomaticPitchLabel));
        OnPropertyChanged(nameof(PitchShiftLabel));
        OnPropertyChanged(nameof(AutomaticOffsetLabel));
        OnPropertyChanged(nameof(EffectiveOffsetLabel));
        OnPropertyChanged(nameof(TempoPairLabel));
    }

    private void RefreshCommandAvailability()
    {
        OnPropertyChanged(nameof(CanSeekPlayback));
        OnPropertyChanged(nameof(CanTuneVocal));
        ProcessTracksCommand.NotifyCanExecuteChanged();
        PickTrackACommand.NotifyCanExecuteChanged();
        PickTrackBCommand.NotifyCanExecuteChanged();
        SwapTracksCommand.NotifyCanExecuteChanged();
        OpenYouTubeSearchCommand.NotifyCanExecuteChanged();
        TrimTrackACommand.NotifyCanExecuteChanged();
        TrimTrackBCommand.NotifyCanExecuteChanged();
        PlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        CancelProcessingCommand.NotifyCanExecuteChanged();
        ExportMixCommand.NotifyCanExecuteChanged();
        TrimAndExportMixCommand.NotifyCanExecuteChanged();
        ResetVocalSpeedCommand.NotifyCanExecuteChanged();
        SetNeutralVocalSpeedCommand.NotifyCanExecuteChanged();
        ApplyAnalysisOverrideCommand.NotifyCanExecuteChanged();
        ResetAnalysisOverrideCommand.NotifyCanExecuteChanged();
    }

    private bool CanProcessTracks() =>
        !IsBusy && TrackA.HasTrack && TrackB.HasTrack;

    private bool CanPickFiles() => !IsBusy;

    private bool CanSwapTracks() => !IsBusy && (TrackA.HasTrack || TrackB.HasTrack);

    private bool CanTrimTrackA() => !IsBusy && TrackA.HasTrack;

    private bool CanTrimTrackB() => !IsBusy && TrackB.HasTrack;

    private bool CanPlayPause() =>
        !IsBusy && HasPreparedMix && IsPlaybackAvailable;

    private bool CanStopPlayback() =>
        !IsBusy && HasPreparedMix && IsPlaybackAvailable;

    private bool CanExportMix() =>
        !IsBusy && HasPreparedMix && _session is not null && _alignment is not null;

    private bool CanCancelOperation() => IsBusy;

    private void OnTrackSlotPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        RefreshTrackLabels();
        RefreshCommandAvailability();
    }

    private void OnThemeChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(IsSystemThemeSelected));
        OnPropertyChanged(nameof(IsLightThemeSelected));
        OnPropertyChanged(nameof(IsDarkThemeSelected));
    }

    private void OnLanguageChanged(object? sender, EventArgs eventArgs)
    {
        StatusMessage = _localization.Format(_statusKey, _statusArguments);
        if (_errorKey is not null)
        {
            ErrorMessage = ComposeError();
        }

        OnPropertyChanged(nameof(FinePitchLabel));
        OnPropertyChanged(nameof(VocalSpeedLabel));
        OnPropertyChanged(nameof(AutomaticSpeedLabel));
        OnPropertyChanged(nameof(OffsetNudgeLabel));
        OnPropertyChanged(nameof(SpeedRatioLabel));
        OnPropertyChanged(nameof(AutomaticPitchLabel));
        OnPropertyChanged(nameof(PitchShiftLabel));
        OnPropertyChanged(nameof(AutomaticOffsetLabel));
        OnPropertyChanged(nameof(EffectiveOffsetLabel));
        OnPropertyChanged(nameof(TempoPairLabel));
        OnPropertyChanged(nameof(PlayPauseLabel));
        OnPropertyChanged(nameof(IsEnglishSelected));
        OnPropertyChanged(nameof(IsRussianSelected));
    }

    private void SetStatus(string key, params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        StatusMessage = _localization.Format(key, arguments);
    }

    private void SetError(string key, string? detail, params object?[] arguments)
    {
        _errorKey = key;
        _errorDetail = detail;
        _errorArguments = arguments;
        ErrorMessage = ComposeError();
    }

    private void ClearError()
    {
        _errorKey = null;
        _errorDetail = null;
        _errorArguments = [];
        ErrorMessage = null;
    }

    private string ComposeError()
    {
        if (_errorKey is null)
        {
            return string.Empty;
        }

        var message = _localization.Format(_errorKey, _errorArguments);
        return string.IsNullOrWhiteSpace(_errorDetail)
            ? message
            : $"{message}{Environment.NewLine}{_errorDetail.Trim()}";
    }

    private void SetErrorFromException(Exception exception)
    {
        var mapped = MapException(exception);
        SetError(mapped.Key, mapped.Detail, mapped.Arguments);
    }

    /// <summary>
    /// Переводит исключение в ключ локализации и необязательные подробности.
    /// UI никогда не показывает сырой стектрейс, но сохраняет причину сбоя.
    /// </summary>
    private static (string Key, object?[] Arguments, string? Detail) MapException(
        Exception exception)
    {
        return exception switch
        {
            ToolUnavailableException tool =>
                ("ErrorToolUnavailable", [tool.ToolName], null),
            YouTubeServiceException youtube =>
                (MapYouTubeError(youtube.Kind), [], YouTubeFailureFormatter.GetTechnicalDetails(youtube)),
            AudioAnalysisException analysis =>
                ("ErrorAnalysisFailed", [], analysis.Message),
            AlignmentException alignment =>
                ("ErrorAlignmentFailed", [], alignment.Message),
            AudioProcessingException processing =>
                ("ErrorProcessingFailed", [], AppendProcessingDiagnostics(processing)),
            FileNotFoundException file =>
                ("ErrorFileNotFound", [], file.FileName),
            NotSupportedException notSupported =>
                ("ErrorUnsupportedFormat", [], notSupported.Message),
            UnauthorizedAccessException =>
                ("ErrorAccessDenied", [], null),
            IOException io =>
                ("ErrorFileOperation", [], io.Message),
            BadImageFormatException or DllNotFoundException =>
                ("ErrorNativeLibrary", [], exception.Message),
            _ => ("ErrorUnexpected", [], exception.Message),
        };
    }

    private static string MapYouTubeError(YouTubeFailureKind kind) => kind switch
    {
        YouTubeFailureKind.VideoUnavailable => "ErrorYouTubeVideoUnavailable",
        YouTubeFailureKind.VideoUnplayable => "ErrorYouTubeVideoUnplayable",
        YouTubeFailureKind.SignInRequired => "ErrorYouTubeSignIn",
        YouTubeFailureKind.AudioStreamUnavailable => "ErrorYouTubeNoAudio",
        YouTubeFailureKind.ConversionFailed => "ErrorYouTubeConversion",
        YouTubeFailureKind.DownloadFailed => "ErrorYouTubeDownload",
        _ => "ErrorYouTubeSearch",
    };

    private static string AppendProcessingDiagnostics(AudioProcessingException exception)
    {
        if (string.IsNullOrWhiteSpace(exception.Diagnostics))
        {
            return exception.Message;
        }

        var diagnostics = exception.Diagnostics
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (diagnostics.Length > 1_500)
        {
            diagnostics = diagnostics[^1_500..];
        }

        return $"{exception.Message}{Environment.NewLine}{diagnostics}";
    }

    /// <summary>
    /// Проверяет путь и расширение до открытия диалога прослушивания.
    /// Это дешёвая ранняя проверка; окончательно формат проверяет декодер.
    /// </summary>
    private static string ValidateAudioFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("No audio file was selected.");
        }

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected audio file no longer exists.", fullPath);
        }

        var extension = Path.GetExtension(fullPath);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException(
                $"Unsupported audio extension '{extension}'. " +
                "Use WAV, MP3, FLAC, M4A, AAC, OGG, OPUS, AIFF or WMA.");
        }

        return fullPath;
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        RefreshCommandAvailability();
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotPlaying));
        OnPropertyChanged(nameof(PlayPauseLabel));
        RefreshCommandAvailability();
    }

    partial void OnHasPreparedMixChanged(bool value) => RefreshCommandAvailability();

    partial void OnIsPlaybackAvailableChanged(bool value) => RefreshCommandAvailability();

    partial void OnIsStemEnhancementEnabledChanged(bool value)
    {
        if (_disposed || !HasPreparedMix)
        {
            return;
        }

        // Улучшение применяется на этапе Prepare, поэтому старый микс больше
        // не соответствует настройке. Пользователь увидит это в статусе.
        ClearPreparedMix();
        ResetTuningControls();
        SetStatus("StatusStemEnhancementChanged");
    }

    partial void OnProgressPercentChanged(double value) =>
        OnPropertyChanged(nameof(ProgressPercentText));

    partial void OnPlaybackProgressChanged(double value)
    {
        if (_suppressPlaybackProgress || _disposed)
        {
            return;
        }

        var duration = _player.Duration;
        PlaybackPositionText = FormatTime(
            TimeSpan.FromSeconds(Math.Clamp(value, 0, 1) * duration.TotalSeconds));

        if (!CanSeekPlayback || _isUserSeeking)
        {
            return;
        }

        SeekToPlaybackProgress(value);
    }

    partial void OnFinePitchSemitonesChanged(double value)
    {
        OnPropertyChanged(nameof(FinePitchLabel));
        OnPropertyChanged(nameof(PitchShiftLabel));

        if (_suppressFinePitchChange)
        {
            return;
        }

        ScheduleVocalRender();
    }

    partial void OnVocalSpeedRatioChanged(double value)
    {
        OnPropertyChanged(nameof(VocalSpeedLabel));

        if (_suppressVocalSpeedChange || _disposed)
        {
            return;
        }

        ScheduleVocalRender();
    }

    partial void OnOffsetNudgeMillisecondsChanged(double value)
    {
        OnPropertyChanged(nameof(OffsetNudgeLabel));
        OnPropertyChanged(nameof(EffectiveOffsetLabel));

        if (_suppressOffsetChange || _session is null || _alignment is null)
        {
            return;
        }

        try
        {
            _player.SetStartOffset(EffectiveOffset);
        }
        catch (Exception exception)
        {
            SetErrorFromException(exception);
        }
    }

    partial void OnInstrumentalVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(InstrumentalVolumeLabel));
        ApplyVolumes();
    }

    partial void OnVocalVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VocalVolumeLabel));
        ApplyVolumes();
    }

    private void ApplyVolumes()
    {
        try
        {
            _player.SetVolumes(InstrumentalVolume, VocalVolume);
        }
        catch (Exception exception)
        {
            SetErrorFromException(exception);
        }
    }

    /// <summary>
    /// Формирует безопасное имя файла из названия инструментала.
    /// Недопустимые для файловой системы символы заменяются дефисом.
    /// </summary>
    private static string BuildExportFileName(MashupSession session)
    {
        var title = session.InstrumentalTrack.Metadata.Title;
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(title
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray())
            .Trim(' ', '.');

        if (safe.Length > 80)
        {
            safe = safe[..80].TrimEnd(' ', '.');
        }

        return string.IsNullOrWhiteSpace(safe)
            ? "mashup.wav"
            : $"{safe} - mashup.wav";
    }

    private string BuildTrimmedExportFileName(MashupSession session)
    {
        var baseName = Path.GetFileNameWithoutExtension(BuildExportFileName(session));
        var suffix = _localization.GetString("ExportTrimmedSuffix");
        return $"{baseName} - {suffix}.wav";
    }

    /// <summary>
    /// Диалог сохранения может вернуть путь без расширения. FFmpeg-обрезчик
    /// принимает только WAV, поэтому добавляем его заранее.
    /// </summary>
    private static string EnsureWavExtension(string path) =>
        string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"{path}.wav";

    private enum ProgressOperation
    {
        Prepare,
        FineTune,
        Export,
    }
}

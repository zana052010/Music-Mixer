// ViewModel модального поиска YouTube.
// Слой Desktop. Ведёт поиск с задержкой, грузит миниатюры, скачивает
// выбранный трек в рабочую область и возвращает результат в MainViewModel.
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicMixer.Application.Abstractions;
using MusicMixer.Application.YouTube;
using MusicMixer.Desktop.Localization;
using MusicMixer.Domain.Exceptions;

namespace MusicMixer.Desktop.YouTube;

/// <summary>
/// Состояние диалога YouTube: строка поиска, результаты с миниатюрами,
/// прогресс загрузки и назначение трека на дорожку A или B.
/// </summary>
public sealed partial class YouTubeSearchViewModel : ObservableObject, IDisposable
{
    private readonly IYouTubeTrackService _youTube;
    private readonly IFilePickerService _filePicker;
    private readonly ILocalizationService _localization;
    private readonly HttpClient _httpClient;
    // Отменяется при закрытии диалога; все операции связаны с этим токеном.
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    // 450 мс ожидания после ввода; отдельный токен — чтобы отменить
    // устаревший поиск, не трогая загрузку.
    private CancellationTokenSource? _debounceCancellation;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _downloadCancellation;
    private string? _outputDirectory;
    private string? _errorKey;
    private string? _errorDetail;
    private object?[] _errorArguments = [];
    // Последняя неудачная попытка из-за проверки входа: после выбора
    // cookies.txt её можно повторить автоматически.
    private YouTubeResultViewModel? _retryItem;
    private TrackTarget _retryTarget;

    // Номер поколения поиска. Ответ старого запроса может прийти позже
    // нового, поэтому результаты сверяются с поколением и отбрасываются.
    private int _searchGeneration;
    private bool _disposed;

    public YouTubeSearchViewModel(
        IYouTubeTrackService youTube,
        IFilePickerService filePicker,
        ILocalizationService localization,
        HttpClient httpClient)
    {
        _youTube = youTube;
        _filePicker = filePicker;
        _localization = localization;
        _httpClient = httpClient;
        _localization.LanguageChanged += OnLanguageChanged;

        // Сервис живёт дольше диалога: ранее выбранный cookies.txt должен
        // оставаться видимым и использоваться при следующем открытии.
        if (!string.IsNullOrWhiteSpace(_youTube.CookieFilePath))
        {
            CookiesFileName = Path.GetFileName(_youTube.CookieFilePath);
        }
    }

    public ObservableCollection<YouTubeResultViewModel> Results { get; } = [];

    [ObservableProperty]
    private string searchQuery = string.Empty;

    [ObservableProperty]
    private bool isSearching;

    [ObservableProperty]
    private bool isDownloading;

    [ObservableProperty]
    private bool hasSearched;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private double downloadProgress;

    [ObservableProperty]
    private string? downloadingTitle;

    [ObservableProperty]
    private string? cookiesFileName;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasCustomCookies => !string.IsNullOrWhiteSpace(CookiesFileName);

    public bool ShowCookiesFileAction =>
        _youTube.SupportsCookieFile &&
        !IsDownloading &&
        !IsSearching &&
        _errorKey is "ErrorYouTubeSignIn" or "ErrorYouTubeCookiesFile";

    public bool CanInteract => !IsDownloading;

    public bool HasResults => Results.Count > 0;

    public bool ShowSearchPrompt => !HasSearched && !IsSearching;

    public bool ShowNoResults =>
        HasSearched &&
        !IsSearching &&
        !HasError &&
        Results.Count == 0;

    public string DownloadStageText => DownloadProgress >= 86
        ? _localization.GetString("YouTubePreparingAudio")
        : _localization.GetString("YouTubeDownloading");

    public string DownloadProgressText =>
        _localization.Format("YouTubeDownloadProgress", DownloadProgress);

    public event Action<YouTubeIngestionResult?>? CloseRequested;

    /// <summary>
    /// Задаёт каталог, куда будут скачиваться WAV. Вызывается сервисом
    /// диалога до показа окна; каталог создаётся заранее.
    /// </summary>
    public void Initialize(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        _outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(_outputDirectory);
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        // Enter и кнопка «Найти» выполняют поиск сразу,
        // без 450-мс задержки.
        CancelDebounce();
        await SearchCoreAsync();
    }

    /// <summary>
    /// Закрывает диалог без выбора.
    /// </summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    /// <summary>
    /// Выбирает cookies.txt и повторяет последнюю загрузку, которой помешала
    /// проверка входа. Это обходит блокировку базы cookies запущенным браузером.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChooseCookiesFile))]
    private async Task ChooseCookiesFileAsync()
    {
        try
        {
            var path = await _filePicker.PickCookiesFileAsync(
                _localization.GetString("YouTubeCookiesFileTitle"),
                _lifetimeCancellation.Token);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            _youTube.SetCookieFile(path);
            CookiesFileName = Path.GetFileName(path);
            ClearError();
            NotifyPresentationState();

            if (_retryItem is { } item)
            {
                var target = _retryTarget;
                _retryItem = null;
                await SelectAsTrackAsync(item, target);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetError("ErrorYouTubeCookiesFile", exception.Message);
        }
    }

    private bool CanChooseCookiesFile() =>
        _youTube.SupportsCookieFile && !IsDownloading && !IsSearching;

    public void CancelOperations()
    {
        _debounceCancellation?.Cancel();
        _searchCancellation?.Cancel();
        _downloadCancellation?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localization.LanguageChanged -= OnLanguageChanged;
        CancelOperations();
        _lifetimeCancellation.Cancel();
        ClearResults();

        _debounceCancellation?.Dispose();
        _searchCancellation?.Dispose();
        _downloadCancellation?.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task SearchCoreAsync()
    {
        var query = SearchQuery.Trim();
        if (query.Length == 0 || IsDownloading)
        {
            return;
        }

        CancelDebounce();
        CancelActiveSearch();
        // Новое поколение делает все ещё летящие ответы неактуальными.
        var generation = ++_searchGeneration;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _searchCancellation = cancellation;

        ClearResults();
        IsSearching = true;
        ClearError();
        NotifyPresentationState();

        try
        {
            var tracks = await _youTube.SearchTracksAsync(query, cancellation.Token);
            // Пока шёл запрос, пользователь мог начать новый поиск.
            // В этом случае старый ответ нельзя показывать.
            if (generation != _searchGeneration || cancellation.IsCancellationRequested)
            {
                return;
            }

            for (var index = 0; index < tracks.Count; index++)
            {
                var result = new YouTubeResultViewModel(
                    tracks[index],
                    index,
                    SelectAsTrackAsync,
                    CanSelect);
                Results.Add(result);
                // Миниатюры грузятся параллельно и не блокируют список.
                _ = LoadThumbnailAsync(result, _lifetimeCancellation.Token);
            }

            HasSearched = true;
            NotifyPresentationState();
        }
        catch (OperationCanceledException)
        {
        }
        catch (YouTubeServiceException exception)
        {
            if (generation == _searchGeneration)
            {
                HasSearched = true;
                SetError(
                    MapYouTubeError(exception.Kind),
                    YouTubeFailureFormatter.GetTechnicalDetails(exception));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (generation == _searchGeneration)
            {
                HasSearched = true;
                SetError("ErrorYouTubeSearch", exception.Message);
            }
        }
        finally
        {
            if (generation == _searchGeneration)
            {
                IsSearching = false;
                NotifyPresentationState();
            }

            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task SelectAsTrackAsync(YouTubeResultViewModel item, TrackTarget target)
    {
        if (_outputDirectory is null || IsDownloading || IsSearching || _disposed)
        {
            return;
        }

        var outputPath = GetAudioOutputPath(item);
        // Повторный выбор уже скачанного видео не тратит трафик:
        // готовый непустой WAV сразу возвращается в MainViewModel.
        if (TryGetCachedAudio(outputPath))
        {
            CloseRequested?.Invoke(new YouTubeIngestionResult(
                item.Track,
                outputPath,
                target));
            return;
        }

        IsDownloading = true;
        DownloadingTitle = item.Title;
        DownloadProgress = 0;
        _retryItem = null;
        ClearError();
        NotifyPresentationState();

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _downloadCancellation = cancellation;

        try
        {
            await _youTube.DownloadAudioStreamAsync(
                item.Track.Id,
                outputPath,
                CreateDownloadProgress(),
                cancellation.Token);

            if (_disposed || cancellation.IsCancellationRequested)
            {
                return;
            }

            CloseRequested?.Invoke(new YouTubeIngestionResult(
                item.Track,
                outputPath,
                target));
        }
        catch (OperationCanceledException)
        {
        }
        catch (YouTubeServiceException exception)
        {
            if (exception.Kind == YouTubeFailureKind.SignInRequired)
            {
                _retryItem = item;
                _retryTarget = target;
            }

            SetError(
                MapYouTubeError(exception.Kind),
                YouTubeFailureFormatter.GetTechnicalDetails(exception));
        }
        catch (ToolUnavailableException exception)
        {
            SetError("ErrorToolUnavailable", null, exception.ToolName);
        }
        catch (OutOfMemoryException)
        {
            SetError("ErrorYouTubeMemory", null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SetError("ErrorYouTubeDownload", exception.Message);
        }
        finally
        {
            IsDownloading = false;
            DownloadProgress = 0;
            DownloadingTitle = null;
            NotifyPresentationState();

            if (ReferenceEquals(_downloadCancellation, cancellation))
            {
                _downloadCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private Progress<double> CreateDownloadProgress() =>
        new(value =>
        {
            // Сервис отдаёт 0..1; UI показывает проценты.
            // 86 % — граница между скачиванием и конвертацией.
            DownloadProgress = Math.Clamp(value, 0, 1) * 100;
            OnPropertyChanged(nameof(DownloadProgressText));
            OnPropertyChanged(nameof(DownloadStageText));
        });

    /// <summary>
    /// Имя файла строится из id видео, а не из названия: id уникален и
    /// не содержит символов, опасных для файловой системы.
    /// </summary>
    private string GetAudioOutputPath(YouTubeResultViewModel item)
    {
        var directory = _outputDirectory
            ?? throw new InvalidOperationException("The YouTube workspace is not initialized.");
        return Path.Combine(
            directory,
            $"{CreateSafeFileName(item.Track.Id)}.wav");
    }

    private static bool TryGetCachedAudio(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private bool CanSearch() =>
        !IsSearching &&
        !IsDownloading &&
        !string.IsNullOrWhiteSpace(SearchQuery);

    private bool CanSelect() => !IsSearching && !IsDownloading;

    partial void OnSearchQueryChanged(string value)
    {
        SearchCommand.NotifyCanExecuteChanged();

        if (_disposed)
        {
            return;
        }

        // Любое изменение текста отменяет и активный поиск, и ожидающий
        // debounce: результаты для старой строки больше не нужны.
        CancelActiveSearch();
        CancelDebounce();

        if (string.IsNullOrWhiteSpace(value))
        {
            ClearResults();
            HasSearched = false;
            ClearError();
            NotifyPresentationState();
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _debounceCancellation = cancellation;
        _ = DebounceSearchAsync(cancellation);
    }

    partial void OnIsSearchingChanged(bool value) => NotifyInteractionState();

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteract));
        NotifyInteractionState();
    }

    partial void OnHasSearchedChanged(bool value) => NotifyPresentationState();

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        NotifyPresentationState();
    }

    partial void OnDownloadProgressChanged(double value)
    {
        OnPropertyChanged(nameof(DownloadProgressText));
        OnPropertyChanged(nameof(DownloadStageText));
    }

    partial void OnCookiesFileNameChanged(string? value) =>
        OnPropertyChanged(nameof(HasCustomCookies));

    private async Task DebounceSearchAsync(CancellationTokenSource cancellation)
    {
        try
        {
            // 450 мс — пауза после последнего нажатия клавиши.
            await Task.Delay(TimeSpan.FromMilliseconds(450), cancellation.Token);
            await SearchCoreAsync();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_debounceCancellation, cancellation))
            {
                _debounceCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task LoadThumbnailAsync(
        YouTubeResultViewModel result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.ThumbnailUrl) || _disposed)
        {
            return;
        }

        try
        {
            using var response = await _httpClient.GetAsync(
                result.ThumbnailUrl,
                // Читаем только заголовки, затем поток: не держим всё
                // изображение в памяти дважды.
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;

            var bitmap = new Bitmap(buffer);
            // Пока грузилась картинка, диалог могли закрыть или список
            // результатов заменить. Тогда bitmap нужно освободить.
            if (_disposed ||
                cancellationToken.IsCancellationRequested ||
                !Results.Contains(result))
            {
                bitmap.Dispose();
                return;
            }

            result.Thumbnail = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The typographic placeholder remains when a remote thumbnail fails.
        }
    }

    private void CancelActiveSearch()
    {
        if (_searchCancellation is null)
        {
            return;
        }

        // Поколение увеличивается до отмены: даже если ответ уже готов,
        // SearchCore увидит несовпадение и не тронет список.
        Interlocked.Increment(ref _searchGeneration);
        _searchCancellation.Cancel();
        _searchCancellation = null;
        if (IsSearching)
        {
            IsSearching = false;
        }
    }

    private void CancelDebounce()
    {
        if (_debounceCancellation is null)
        {
            return;
        }

        _debounceCancellation.Cancel();
        _debounceCancellation = null;
    }

    private void NotifyInteractionState()
    {
        SearchCommand.NotifyCanExecuteChanged();
        foreach (var result in Results)
        {
            result.NotifySelectionState();
        }

        NotifyPresentationState();
    }

    private void NotifyPresentationState()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowSearchPrompt));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowCookiesFileAction));
        OnPropertyChanged(nameof(HasCustomCookies));
        ChooseCookiesFileCommand.NotifyCanExecuteChanged();
    }

    private void ClearResults()
    {
        // Строки владеют Bitmap-ами, поэтому их нужно освободить,
        // а не просто очистить коллекцию.
        foreach (var result in Results)
        {
            result.Dispose();
        }

        Results.Clear();
        _retryItem = null;
        NotifyPresentationState();
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

    private void OnLanguageChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(DownloadStageText));
        OnPropertyChanged(nameof(DownloadProgressText));

        if (_errorKey is not null)
        {
            ErrorMessage = ComposeError();
        }
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

    private static string CreateSafeFileName(string videoId)
    {
        var characters = videoId
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .ToArray();
        return characters.Length == 0
            ? Guid.NewGuid().ToString("N")
            : new string(characters);
    }
}

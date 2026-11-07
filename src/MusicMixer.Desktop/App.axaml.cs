// Корень композиции настольного приложения на Microsoft DI.
// Слой Desktop: связывает интерфейсы Application с реализациями Infrastructure и Desktop.
// Вызывается Avalonia после загрузки App.axaml; создаёт MainWindow и его MainViewModel.
using System.Net.Http;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using MusicMixer.Application.Abstractions;
using MusicMixer.Application.Mashup;
using MusicMixer.Desktop.Audition;
using MusicMixer.Desktop.Localization;
using MusicMixer.Desktop.Services;
using MusicMixer.Desktop.Trimming;
using MusicMixer.Desktop.ViewModels;
using MusicMixer.Desktop.Views;
using MusicMixer.Desktop.YouTube;
using MusicMixer.Infrastructure.Analysis;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Playback;
using MusicMixer.Infrastructure.Processes;
using MusicMixer.Infrastructure.Processing;
using MusicMixer.Infrastructure.Separation;
using MusicMixer.Infrastructure.Workspaces;
using MusicMixer.Infrastructure.YouTube;

namespace MusicMixer.Desktop;

/// <summary>
/// Корень композиции приложения: создаёт DI-контейнер, главное окно и
/// освобождает сервисы при завершении процесса.
/// </summary>
public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;

    /// <summary>
    /// Загружает App.axaml с ресурсами и стилями до создания окна.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Для классического desktop-lifetime строит контейнер и назначает MainWindow;
    /// при выходе освобождает провайдер, останавливая плеер и временные ресурсы.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = BuildServiceProvider(desktop);
            desktop.MainWindow = _services.GetRequiredService<MainWindow>();
            desktop.Exit += (_, _) =>
            {
                // Освобождение контейнера останавливает BASS и удаляет временные workspace.
                _services?.Dispose();
                _services = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Регистрирует реализации Application и Infrastructure, а также desktop-адаптеры.
    /// Настройки инструментов читаются из окружения до создания сервисов.
    /// </summary>
    private static ServiceProvider BuildServiceProvider(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Единственный экземпляр: XAML-расширение Localize обращается к нему напрямую,
        // поэтому DI должен вернуть тот же объект, иначе привязки потеряют смену языка.
        var localization = LocalizationManager.Instance;
        localization.SetLanguage(LocalizationManager.GetSystemDefaultCulture());

        // Пути к ffmpeg, yt-dlp и Demucs фиксируются один раз, чтобы не искать
        // инструменты в PATH и не читать переменные окружения на каждый запуск.
        var ffmpegOptions = FfmpegOptions.FromEnvironment();
        var demucsOptions = DemucsOptions.FromEnvironment();
        var ytDlpOptions = YtDlpOptions.FromEnvironment();
        var analysisOptions = new AudioAnalysisOptions();
        AddFfmpegDirectoryToProcessPath(ffmpegOptions.ExecutablePath);

        var services = new ServiceCollection();

        services.AddSingleton(ffmpegOptions);
        services.AddSingleton(demucsOptions);
        services.AddSingleton(ytDlpOptions);
        services.AddSingleton(analysisOptions);
        // Нужен сервисам диалогов и файлового пикера, чтобы получить окно-владелец.
        services.AddSingleton<IClassicDesktopStyleApplicationLifetime>(desktop);
        services.AddSingleton<ILocalizationService>(localization);
        services.AddSingleton<IThemeService, ThemeService>();
        // Один HttpClient на процесс: сокеты переиспользуются, а таймаут
        // не даёт зависнуть загрузке обложек и запросам YouTube.
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        // Общий раннер внешних процессов: единые отмена, таймауты и перехват вывода.
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IAudioAnalyzerService>(
            _ => new AudioAnalyzerService(ffmpegOptions, analysisOptions));
        // Волновая форма строится тем же FFmpeg, но на низкой частоте.
        services.AddSingleton<IAudioWaveformService>(
            _ => new FfmpegWaveformService(ffmpegOptions));
        // Demucs выделяет инструментал A и вокал B; стемы читает MashupWorkflow.
        services.AddSingleton<IAudioSeparatorService>(
            provider => new DemucsCliSeparatorService(
                provider.GetRequiredService<IProcessRunner>(),
                demucsOptions));
        // Rubber Band меняет темп и тональность вокала под сетку инструментала.
        services.AddSingleton<IAudioProcessingService>(
            provider => new RubberBandAlignmentService(
                provider.GetRequiredService<IProcessRunner>(),
                ffmpegOptions));
        // Обрезка выполняется до анализа, чтобы BPM и даунбит считались по выбранному фрагменту.
        services.AddSingleton<IAudioTrimmerService>(
            provider => new FfmpegAudioTrimmerService(
                provider.GetRequiredService<IProcessRunner>(),
                ffmpegOptions));
        // Сведение готового микса в один WAV для скачивания.
        services.AddSingleton<IMixdownService>(
            provider => new FfmpegMixdownService(
                provider.GetRequiredService<IProcessRunner>(),
                ffmpegOptions));
        // Мягкое улучшение извлечённых стемов: EQ, компрессия и лимитер.
        services.AddSingleton<IAudioStemEnhancerService>(
            provider => new FfmpegStemEnhancerService(
                provider.GetRequiredService<IProcessRunner>(),
                ffmpegOptions));
        // BASS держит общий микс: инструментал и вокал стартуют по одному транспорту.
        services.AddSingleton<IAudioPlayerService, ManagedBassAudioPlayerService>();
        // Временные workspace изолируют промежуточные wav-файлы и удаляются при выходе.
        services.AddSingleton<IWorkspaceFactory, TemporaryWorkspaceFactory>();
        // Сценарий мэшапа связывает анализ, разделение, выравнивание и подготовку результата.
        services.AddSingleton<IMashupWorkflow, MashupWorkflow>();
        // Адаптер скрывает Avalonia StorageProvider от ViewModel.
        services.AddSingleton<IFilePickerService>(
            _ => new StorageProviderFilePickerService(desktop));
        // Поиск и загрузка YouTube идут через yt-dlp, конвертация в WAV — через ffmpeg.
        services.AddSingleton<IYouTubeTrackService>(
            provider => new YtDlpTrackService(
                provider.GetRequiredService<IProcessRunner>(),
                ffmpegOptions,
                ytDlpOptions));
        // Сервисы диалогов не хранят состояние и живут всё время работы приложения.
        services.AddSingleton<IYouTubeDialogService, YouTubeSearchDialogService>();
        services.AddTransient<YouTubeSearchViewModel>();
        services.AddSingleton<IAudioTrimDialogService, AudioTrimDialogService>();
        services.AddTransient<AudioTrimViewModel>();
        services.AddSingleton<IAudioAuditionDialogService, AudioAuditionDialogService>();
        // Выбор пути для скачивания готового микса.
        services.AddSingleton<IAudioExportDialogService, AudioExportDialogService>();
        // ViewModel диалога transient: каждый показ должен начинаться с чистого состояния.
        services.AddTransient<AudioAuditionViewModel>();
        // Главное окно и его VM создаются один раз и живут до завершения приложения.
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Добавляет каталог ffmpeg в начало PATH текущего процесса.
    /// Нужно потому, что Demucs вызывает ffmpeg и ffprobe по имени.
    /// </summary>
    private static void AddFfmpegDirectoryToProcessPath(string ffmpegExecutablePath)
    {
        if (!Path.IsPathRooted(ffmpegExecutablePath))
        {
            // Относительный путь означает доверие PATH; добавлять нечего.
            return;
        }

        var directory = Path.GetDirectoryName(ffmpegExecutablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var entries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Any(entry => string.Equals(entry, directory, comparison)))
        {
            // Не дублируем каталог, чтобы PATH не разрастался при повторной инициализации.
            return;
        }

        // Demucs calls ffmpeg/ffprobe by name for decoding and encoding.
        // Меняем PATH только текущего процесса: системные переменные Windows не трогаем.
        Environment.SetEnvironmentVariable(
            "PATH",
            directory + Path.PathSeparator + string.Join(Path.PathSeparator, entries),
            EnvironmentVariableTarget.Process);
    }
}

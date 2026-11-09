// Настройки внешних инструментов и анализа. Слой Infrastructure.
// Значения по умолчанию можно переопределить переменными окружения;
// экрана настроек пока нет. Читается в App.axaml.cs при старте приложения.
namespace MusicMixer.Infrastructure.Configuration;

/// <summary>
/// Параметры FFmpeg: путь к бинарнику и формат вывода.
/// </summary>
public sealed record FfmpegOptions
{
    public string ExecutablePath { get; init; } = "ffmpeg";

    // 48 кГц stereo PCM16 — единый рабочий формат всех стемов и предпросмотра.
    public int OutputSampleRate { get; init; } = 48_000;

    public int OutputChannels { get; init; } = 2;

    /// <summary>
    /// Определяет FFmpeg в таком порядке: переменная MUSICMIXER_FFMPEG,
    /// встроенный tools/ffmpeg/bin рядом с приложением, затем PATH.
    /// </summary>
    public static FfmpegOptions FromEnvironment()
    {
        var configuredPath = Environment.GetEnvironmentVariable("MUSICMIXER_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return new FfmpegOptions { ExecutablePath = configuredPath.Trim() };
        }

        var bundledPath = FindBundledExecutable();
        return new FfmpegOptions
        {
            ExecutablePath = bundledPath ?? new FfmpegOptions().ExecutablePath,
        };
    }

    /// <summary>
    /// Ищет локальную сборку FFmpeg, чтобы пользователю не нужно было
    /// менять системный PATH. Если файла нет, останется "ffmpeg" из PATH.
    /// </summary>
    private static string? FindBundledExecutable()
    {
        var fileName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// Параметры Demucs: путь, модель и необязательные настройки устройства.
/// </summary>
public sealed record DemucsOptions
{
    public string ExecutablePath { get; init; } = "demucs";

    public string ModelName { get; init; } = "htdemucs";

    /// <summary>
    /// Необязательное устройство torch: "cpu", "cuda", "mps" и т. п.
    /// null означает выбор самого Demucs.
    /// </summary>
    public string? Device { get; init; }

    /// <summary>Число параллельных заданий. 0 — значение Demucs по умолчанию.</summary>
    public int Jobs { get; init; }

    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Читает MUSICMIXER_DEMUCS, MUSICMIXER_DEMUCS_MODEL и
    /// MUSICMIXER_DEMUCS_DEVICE, затем ищет локальное окружение .venv.
    /// </summary>
    public static DemucsOptions FromEnvironment()
    {
        var configuredPath = Environment.GetEnvironmentVariable("MUSICMIXER_DEMUCS");
        var executablePath = !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath.Trim()
            : FindBundledExecutable() ?? new DemucsOptions().ExecutablePath;

        return new DemucsOptions
        {
            ExecutablePath = executablePath,
            ModelName = Environment.GetEnvironmentVariable("MUSICMIXER_DEMUCS_MODEL")
                ?? new DemucsOptions().ModelName,
            Device = NullIfWhiteSpace(Environment.GetEnvironmentVariable("MUSICMIXER_DEMUCS_DEVICE")),
        };
    }

    /// <summary>
    /// Ищет demucs.exe в .venv проекта, затем в tools/demucs, поднимаясь
    /// от каталога приложения вверх до восьми уровней. Это покрывает запуск
    /// из bin/Debug, из publish и из корня репозитория.
    /// </summary>
    private static string? FindBundledExecutable()
    {
        var fileName = OperatingSystem.IsWindows() ? "demucs.exe" : "demucs";
        var venvScripts = OperatingSystem.IsWindows() ? "Scripts" : "bin";
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, ".venv", venvScripts, fileName),
            Path.Combine(AppContext.BaseDirectory, "tools", "demucs", fileName),
        };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, ".venv", venvScripts, fileName));
        }

        candidates.Add(Path.Combine(
            Environment.CurrentDirectory,
            ".venv",
            venvScripts,
            fileName));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Параметры YouTube (YoutubeExplode). Внешние программы не нужны;
/// настраивается только необязательный cookies.txt в формате Netscape.
/// </summary>
public sealed record YouTubeOptions
{
    /// <summary>
    /// Файл cookies.txt для YouTube: помогает при проверке «Sign in to confirm
    /// you're not a bot» и ограничении запросов из сети.
    /// </summary>
    public string? CookiesFile { get; init; }

    /// <summary>
    /// Читает MUSICMIXER_YOUTUBE_COOKIES, а для совместимости со старыми
    /// настройками — MUSICMIXER_YTDLP_COOKIES.
    /// </summary>
    public static YouTubeOptions FromEnvironment() => new()
    {
        CookiesFile = NullIfWhiteSpace(Environment.GetEnvironmentVariable("MUSICMIXER_YOUTUBE_COOKIES"))
            ?? NullIfWhiteSpace(Environment.GetEnvironmentVariable("MUSICMIXER_YTDLP_COOKIES")),
    };

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Параметры управляемого анализа. Ограничение MaxDuration защищает от
/// многоминутного DSP на длинных файлах; Demucs при этом обрабатывает весь файл.
/// </summary>
public sealed record AudioAnalysisOptions
{
    public int SampleRate { get; init; } = 22_050;

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(10);

    public double MinimumBpm { get; init; } = 60;

    public double MaximumBpm { get; init; } = 200;

    // Минимум долей, чтобы детектор даунбита не сработал на коротком фрагменте.
    public int MinimumDownbeatBeats { get; init; } = 8;
}

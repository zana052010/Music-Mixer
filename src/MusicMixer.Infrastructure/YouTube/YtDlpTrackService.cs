// Загрузка аудио с YouTube через yt-dlp + FFmpeg.
// Слой Infrastructure; реализует IYouTubeTrackService. Вызывается из
// YouTubeSearchViewModel. yt-dlp запускается тем же ProcessRunner, что и
// остальные CLI-инструменты; наружу отдаётся только готовый WAV.
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MusicMixer.Application.Abstractions;
using MusicMixer.Application.YouTube;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processes;

namespace MusicMixer.Infrastructure.YouTube;

/// <summary>
/// Адаптер поиска и загрузки YouTube на yt-dlp. Поиск использует плейлист
/// ytsearch без скачивания медиа, загрузка берёт лучший аудиопоток, а WAV
/// по-прежнему собирает тот же FFmpeg, что и остальные стадии конвейера.
/// </summary>
public sealed partial class YtDlpTrackService : IYouTubeTrackService
{
    private const int SearchResultLimit = 15;

    // yt-dlp печатает прогресс по шаблону одной строкой на обновление,
    // поэтому разбор не зависит от локали и формата процентов.
    private const string ProgressTemplate =
        "download:@@%(progress.downloaded_bytes)s@@%(progress.total_bytes)s@@%(progress.total_bytes_estimate)s";

    private readonly IProcessRunner _processRunner;
    private readonly FfmpegOptions _ffmpegOptions;
    private readonly YtDlpOptions _ytDlpOptions;
    private readonly Func<string?> _defaultBrowserProvider;
    private readonly string? _javaScriptRuntime;
    private string? _cookieFilePath;

    public YtDlpTrackService(
        IProcessRunner processRunner,
        FfmpegOptions ffmpegOptions,
        YtDlpOptions ytDlpOptions,
        Func<string?>? defaultBrowserProvider = null)
    {
        _processRunner = processRunner;
        _ffmpegOptions = ffmpegOptions;
        _ytDlpOptions = ytDlpOptions;
        _defaultBrowserProvider =
            defaultBrowserProvider ?? BrowserCookieLocator.FindDefaultBrowser;
        _javaScriptRuntime =
            ytDlpOptions.JavaScriptRuntime ?? JavaScriptRuntimeLocator.Find();
    }

    public bool SupportsCookieFile => true;

    public string? CookieFilePath => _cookieFilePath ?? _ytDlpOptions.CookiesFile;

    public void SetCookieFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _cookieFilePath = null;
            return;
        }

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The cookies file does not exist.", fullPath);
        }

        _cookieFilePath = fullPath;
    }

    public async Task<IReadOnlyList<YouTubeTrackItem>> SearchTracksAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedQuery.Length == 0)
        {
            return [];
        }

        var results = new List<YouTubeTrackItem>(SearchResultLimit);
        var request = new ProcessLaunchRequest(
            _ytDlpOptions.ExecutablePath,
            [
                .. BuildBaseArguments(),
                "--flat-playlist",
                "--dump-json",
                $"ytsearch{SearchResultLimit}:{normalizedQuery}",
            ]);

        var result = await _processRunner.RunAsync(
            request,
            onOutputLine: line =>
            {
                if (!line.IsError &&
                    TryParseSearchEntry(line.Text, out var item) &&
                    results.Count < SearchResultLimit)
                {
                    results.Add(item);
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new YouTubeServiceException(
                YouTubeFailureKind.SearchFailed,
                "The yt-dlp search request failed.",
                diagnostics: result.OutputTail);
        }

        return results;
    }

    public async Task DownloadAudioStreamAsync(
        string videoId,
        string outputFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);
        if (!IsValidVideoId(videoId))
        {
            throw new ArgumentException(
                "The value is not a YouTube video id.",
                nameof(videoId));
        }

        var finalPath = Path.GetFullPath(outputFilePath);
        if (!string.Equals(Path.GetExtension(finalPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("YouTube ingestion must produce a .wav file.", nameof(outputFilePath));
        }

        var outputDirectory = Path.GetDirectoryName(finalPath)
            ?? throw new ArgumentException("The output file needs a parent directory.", nameof(outputFilePath));
        Directory.CreateDirectory(outputDirectory);

        var runId = Guid.NewGuid().ToString("N");
        // Скачиваем и конвертируем во временные файлы с уникальным именем:
        // параллельные попытки не перезапишут друг друга.
        var temporaryDownloadPath = Path.Combine(
            outputDirectory,
            $".youtube-{runId}.download");
        var temporaryWavePath = Path.Combine(
            outputDirectory,
            $".youtube-{runId}.wav");

        try
        {
            // Проверяем FFmpeg до загрузки: иначе пользователь скачает
            // десятки мегабайт и только потом узнает, что конвертер недоступен.
            await EnsureFfmpegAvailableAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(0.01);

            var scaledProgress = new MonotonicProgress(
                new ScaledProgress(progress, 0.03, 0.82));
            var result = await RunDownloadAsync(
                videoId,
                temporaryDownloadPath,
                browserCookies: null,
                useTelevisionClient: true,
                scaledProgress,
                cancellationToken).ConfigureAwait(false);

            var usedBrowserCookies = false;
            if (result.ExitCode != 0 &&
                ClassifyFailureOutput(result.OutputTail) == YouTubeFailureKind.SignInRequired)
            {
                var browser = ResolveAutoCookieBrowser();
                if (browser is not null)
                {
                    usedBrowserCookies = true;
                    TryDelete(temporaryDownloadPath);
                    // TV-клиент и cookies несовместимы: для авторизованной
                    // попытки возвращаемся к обычному клиенту yt-dlp.
                    result = await RunDownloadAsync(
                        videoId,
                        temporaryDownloadPath,
                        browser,
                        useTelevisionClient: false,
                        scaledProgress,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (result.ExitCode != 0)
            {
                var kind = ClassifyFailureOutput(result.OutputTail);
                if (usedBrowserCookies && kind == YouTubeFailureKind.DownloadFailed)
                {
                    // База cookies Chromium может быть занята запущенным браузером.
                    // Пользователю нужен совет, а не общая ошибка загрузки.
                    kind = YouTubeFailureKind.SignInRequired;
                }

                throw new YouTubeServiceException(
                    kind,
                    "yt-dlp could not download the requested video.",
                    diagnostics: result.OutputTail);
            }

            var downloaded = new FileInfo(temporaryDownloadPath);
            if (!downloaded.Exists || downloaded.Length == 0)
            {
                throw new YouTubeServiceException(
                    YouTubeFailureKind.DownloadFailed,
                    "The downloaded YouTube audio stream was empty.");
            }

            progress?.Report(0.86);
            await ConvertToWaveAsync(
                temporaryDownloadPath,
                temporaryWavePath,
                cancellationToken).ConfigureAwait(false);

            // Публикуем только полностью готовый WAV. При неудачной повторной
            // попытке ранее скачанный корректный файл остаётся нетронутым.
            File.Move(temporaryWavePath, finalPath, overwrite: true);

            var output = new FileInfo(finalPath);
            if (!output.Exists || output.Length == 0)
            {
                throw new YouTubeServiceException(
                    YouTubeFailureKind.ConversionFailed,
                    "The converted YouTube audio file was not created.");
            }

            progress?.Report(1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (YouTubeServiceException)
        {
            throw;
        }
        catch (ToolUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new YouTubeServiceException(
                YouTubeFailureKind.DownloadFailed,
                "YouTube audio could not be downloaded or converted.",
                exception);
        }
        finally
        {
            // Временные файлы удаляются всегда: и при успехе, и при отмене,
            // и при ошибке. Итоговый файл уже опубликован через Move.
            TryDelete(temporaryDownloadPath);
            TryDelete(temporaryWavePath);
        }
    }

    /// <summary>
    /// Общие аргументы yt-dlp: изолированная конфигурация, JavaScript runtime
    /// и cookies, если пользователь настроил обход бот-проверки YouTube.
    /// </summary>
    private IReadOnlyList<string> BuildBaseArguments()
    {
        var arguments = new List<string>
        {
            "--ignore-config",
            "--no-warnings",
            "--encoding", "utf-8",
        };

        if (!string.IsNullOrWhiteSpace(_javaScriptRuntime))
        {
            arguments.AddRange(["--js-runtimes", _javaScriptRuntime]);
        }

        if (!string.IsNullOrWhiteSpace(_cookieFilePath))
        {
            arguments.AddRange(["--cookies", _cookieFilePath]);
        }
        else if (!string.IsNullOrWhiteSpace(_ytDlpOptions.CookiesFile))
        {
            arguments.AddRange(["--cookies", _ytDlpOptions.CookiesFile]);
        }

        if (!string.IsNullOrWhiteSpace(_ytDlpOptions.CookiesFromBrowser))
        {
            arguments.AddRange(["--cookies-from-browser", _ytDlpOptions.CookiesFromBrowser]);
        }

        return arguments;
    }

    /// <summary>
    /// Запускает одну попытку загрузки. TV-клиент обходит проверку
    /// proof-of-origin для обычных видео; для авторизованной попытки
    /// используется стандартный клиент, совместимый с cookies.
    /// </summary>
    private async Task<ProcessExecutionResult> RunDownloadAsync(
        string videoId,
        string temporaryDownloadPath,
        string? browserCookies,
        bool useTelevisionClient,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(BuildBaseArguments())
        {
            "--no-playlist",
            "-f", "bestaudio/best",
        };

        if (useTelevisionClient)
        {
            arguments.AddRange(
                ["--extractor-args", "youtube:player_client=tv,web_safari"]);
        }

        if (!string.IsNullOrWhiteSpace(browserCookies))
        {
            arguments.AddRange(["--cookies-from-browser", browserCookies]);
        }

        arguments.AddRange(
            [
                "--newline",
                "--progress-template", ProgressTemplate,
                "-o", temporaryDownloadPath,
                $"https://www.youtube.com/watch?v={videoId}",
            ]);

        return await _processRunner.RunAsync(
            new ProcessLaunchRequest(_ytDlpOptions.ExecutablePath, arguments),
            onOutputLine: line =>
            {
                if (line.IsError || !TryParseProgressLine(line.Text, out var fraction))
                {
                    return;
                }

                progress.Report(fraction);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Возвращает браузер для автоматической повторной попытки только тогда,
    /// когда пользователь не задал cookies явно и fallback не отключён.
    /// </summary>
    private string? ResolveAutoCookieBrowser()
    {
        if (!_ytDlpOptions.AutoCookiesFromBrowser ||
            !string.IsNullOrWhiteSpace(_cookieFilePath) ||
            !string.IsNullOrWhiteSpace(_ytDlpOptions.CookiesFile) ||
            !string.IsNullOrWhiteSpace(_ytDlpOptions.CookiesFromBrowser))
        {
            return null;
        }

        try
        {
            return _defaultBrowserProvider();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Быстрая проверка, что FFmpeg вообще запускается. Выполняется до
    /// запроса манифеста и скачивания.
    /// </summary>
    private async Task EnsureFfmpegAvailableAsync(CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            new ProcessLaunchRequest(
                _ffmpegOptions.ExecutablePath,
                ["-hide_banner", "-version"]),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new ToolUnavailableException(
                _ffmpegOptions.ExecutablePath,
                new InvalidOperationException(result.OutputTail));
        }
    }

    /// <summary>
    /// Конвертирует скачанный контейнер в WAV PCM16 48 кГц, стерео — тот же
    /// формат, что у локально импортированных файлов и стемов.
    /// </summary>
    private async Task ConvertToWaveAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i", inputPath,
            "-map", "0:a:0",
            "-vn",
            "-sn",
            "-dn",
            "-ar", "48000",
            "-ac", "2",
            "-c:a", "pcm_s16le",
            outputPath,
        };

        var result = await _processRunner.RunAsync(
            new ProcessLaunchRequest(_ffmpegOptions.ExecutablePath, arguments),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new YouTubeServiceException(
                YouTubeFailureKind.ConversionFailed,
                $"FFmpeg could not convert the YouTube audio stream (exit code {result.ExitCode}).",
                diagnostics: result.OutputTail);
        }

        var output = new FileInfo(outputPath);
        if (!output.Exists || output.Length == 0)
        {
            throw new YouTubeServiceException(
                YouTubeFailureKind.ConversionFailed,
                "FFmpeg finished without producing a usable audio file.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Разбирает одну NDJSON-строку поиска yt-dlp. Неизвестные или пустые
    /// строки пропускаются: ytsearch может отдать служебные записи.
    /// </summary>
    internal static bool TryParseSearchEntry(
        string jsonLine,
        [NotNullWhen(true)] out YouTubeTrackItem? item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(jsonLine) || !jsonLine.StartsWith('{'))
        {
            return false;
        }

        JsonElement entry;
        try
        {
            entry = JsonDocument.Parse(jsonLine).RootElement;
        }
        catch (JsonException)
        {
            return false;
        }

        if (entry.ValueKind != JsonValueKind.Object ||
            entry.TryGetProperty("id", out var idElement) is false ||
            idElement.ValueKind is not JsonValueKind.String ||
            entry.TryGetProperty("title", out var titleElement) is false ||
            titleElement.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        var id = idElement.GetString();
        var title = titleElement.GetString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var author = GetOptionalString(entry, "channel")
            ?? GetOptionalString(entry, "uploader");
        TimeSpan? duration = GetOptionalNumber(entry, "duration") is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : null;

        item = new YouTubeTrackItem(
            id,
            title,
            author ?? "YouTube",
            duration,
            GetThumbnailUrl(entry, id));
        return true;
    }

    /// <summary>
    /// Разбирает строку прогресса загрузки, собранную по ProgressTemplate.
    /// Части шаблона разделяются маркером "@@"; NA означает отсутствие числа.
    /// </summary>
    internal static bool TryParseProgressLine(string line, out double fraction)
    {
        fraction = 0;
        const string prefix = "download:";
        if (line is null || !line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = line[prefix.Length..].Split("@@");
        if (parts.Length < 4 ||
            !TryParseBytes(parts[1], out var downloadedBytes))
        {
            return false;
        }

        var totalBytes = TryParseBytes(parts[2], out var total) && total > 0
            ? total
            : TryParseBytes(parts[3], out var estimate) ? estimate : 0;
        if (totalBytes <= 0)
        {
            return false;
        }

        fraction = Math.Clamp(downloadedBytes / (double)totalBytes, 0, 1);
        return true;
    }

    /// <summary>
    /// Относит причину сбоя yt-dlp к категории для локализованного совета UI.
    /// Формулировки stderr YouTube стабильны, но проверяются по подстрокам:
    /// точный текст со временем меняется, поэтому всё остальное считается
    /// общей неудачей загрузки, а не недоступностью видео.
    /// </summary>
    internal static YouTubeFailureKind ClassifyFailureOutput(string outputTail)
    {
        var tail = outputTail ?? string.Empty;
        if (tail.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("Private video", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("removed by the uploader", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("account associated with this video has been terminated",
                StringComparison.OrdinalIgnoreCase))
        {
            return YouTubeFailureKind.VideoUnavailable;
        }

        if (tail.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("cookies for the authentication", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("confirm your age", StringComparison.OrdinalIgnoreCase))
        {
            return YouTubeFailureKind.SignInRequired;
        }

        if (tail.Contains("age-restricted", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("members-only", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("Join this channel", StringComparison.OrdinalIgnoreCase))
        {
            return YouTubeFailureKind.VideoUnplayable;
        }

        return YouTubeFailureKind.DownloadFailed;
    }

    /// <summary>
    /// yt-dlp получает id внутри URL, поэтому идентификатор валидируется до
    /// запуска: чужая строка не должна превратиться в аргумент командной строки.
    /// </summary>
    internal static bool IsValidVideoId(string videoId) =>
        VideoIdRegex().IsMatch(videoId);

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    private static string? GetOptionalString(JsonElement entry, string propertyName) =>
        entry.TryGetProperty(propertyName, out var element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static double? GetOptionalNumber(JsonElement entry, string propertyName) =>
        entry.TryGetProperty(propertyName, out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetDouble(out var value)
            ? value
            : null;

    private static string GetThumbnailUrl(JsonElement entry, string videoId) =>
        entry.TryGetProperty("thumbnails", out var thumbnails) &&
        thumbnails.ValueKind == JsonValueKind.Array
            ? thumbnails.EnumerateArray()
                .Select(thumbnail => new
                {
                    url = GetOptionalString(thumbnail, "url"),
                    area = (GetOptionalNumber(thumbnail, "width") ?? 0)
                           * (GetOptionalNumber(thumbnail, "height") ?? 0),
                })
                .Where(thumbnail => !string.IsNullOrWhiteSpace(thumbnail.url))
                .OrderByDescending(thumbnail => thumbnail.area)
                .Select(thumbnail => thumbnail.url)
                .FirstOrDefault()
            ?? $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg"
            : $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg";

    private static bool TryParseBytes(string? text, out long value) =>
        long.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);

    /// <summary>
    /// Переводит прогресс загрузки yt-dlp (0..1) в его долю общего прогресса
    /// импорта, оставляя место конвертации.
    /// </summary>
    private sealed class ScaledProgress : IProgress<double>
    {
        private readonly IProgress<double>? _inner;
        private readonly double _start;
        private readonly double _end;

        public ScaledProgress(IProgress<double>? inner, double start, double end)
        {
            _inner = inner;
            _start = start;
            _end = end;
        }

        public void Report(double value)
        {
            _inner?.Report(_start + (Math.Clamp(value, 0, 1) * (_end - _start)));
        }
    }

    /// <summary>
    /// Не даёт индикатору откатиться назад, если после бот-проверки
    /// загрузка повторяется с cookies и снова начинает считать проценты.
    /// </summary>
    private sealed class MonotonicProgress : IProgress<double>
    {
        private readonly IProgress<double> _inner;
        private double _last;

        public MonotonicProgress(IProgress<double> inner)
        {
            _inner = inner;
        }

        public void Report(double value)
        {
            var normalized = Math.Clamp(value, 0, 1);
            if (normalized <= _last)
            {
                return;
            }

            _last = normalized;
            _inner.Report(normalized);
        }
    }
}

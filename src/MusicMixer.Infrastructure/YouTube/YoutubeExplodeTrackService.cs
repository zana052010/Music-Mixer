// Поиск и загрузка аудио с YouTube через YoutubeExplode + FFmpeg.
// Слой Infrastructure; реализует IYouTubeTrackService. Вызывается из
// YouTubeSearchViewModel. Внешних программ для YouTube больше нет:
// YoutubeExplode работает внутри процесса, наружу отдаётся только готовый WAV.
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MusicMixer.Application.Abstractions;
using MusicMixer.Application.YouTube;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processes;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Videos.Streams;

namespace MusicMixer.Infrastructure.YouTube;

/// <summary>
/// Адаптер YouTube на YoutubeExplode. Поиск и выбор аудиопотока идут через
/// внутреннее API YouTube, а WAV по-прежнему собирает тот же FFmpeg, что и
/// остальные стадии конвейера.
/// </summary>
public sealed partial class YoutubeExplodeTrackService : IYouTubeTrackService, IDisposable
{
    private const int SearchResultLimit = 15;

    private readonly IProcessRunner _processRunner;
    private readonly FfmpegOptions _ffmpegOptions;
    private readonly Func<IReadOnlyList<Cookie>, YoutubeClient> _clientFactory;
    private readonly object _clientLock = new();
    private YoutubeClient _client;
    private string? _cookieFilePath;
    private bool _disposed;

    public YoutubeExplodeTrackService(
        IProcessRunner processRunner,
        FfmpegOptions ffmpegOptions,
        YouTubeOptions youTubeOptions,
        Func<IReadOnlyList<Cookie>, YoutubeClient>? clientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(ffmpegOptions);
        ArgumentNullException.ThrowIfNull(youTubeOptions);

        _processRunner = processRunner;
        _ffmpegOptions = ffmpegOptions;
        _clientFactory = clientFactory ?? (static cookies => new YoutubeClient(cookies));

        IReadOnlyList<Cookie> cookies = [];
        if (!string.IsNullOrWhiteSpace(youTubeOptions.CookiesFile))
        {
            // Битый файл из переменной окружения не должен ронять запуск:
            // приложение просто работает без cookies.
            try
            {
                var fullPath = Path.GetFullPath(youTubeOptions.CookiesFile);
                var parsed = ParseNetscapeCookies(File.ReadAllLines(fullPath));
                if (parsed.Count > 0)
                {
                    cookies = parsed;
                    _cookieFilePath = fullPath;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        _client = _clientFactory(cookies);
    }

    public bool SupportsCookieFile => true;

    public string? CookieFilePath => _cookieFilePath;

    /// <summary>
    /// Читает cookies.txt в формате Netscape и пересоздаёт клиент YouTube с
    /// этими cookies. Вызывается из диалога, когда поиск и загрузка не идут.
    /// </summary>
    public void SetCookieFile(string? filePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            ReplaceClient([], null);
            return;
        }

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The cookies file does not exist.", fullPath);
        }

        var cookies = ParseNetscapeCookies(File.ReadAllLines(fullPath));
        if (cookies.Count == 0)
        {
            throw new InvalidDataException(
                "The cookies file does not contain youtube.com or google.com cookies in Netscape format.");
        }

        ReplaceClient(cookies, fullPath);
    }

    public async Task<IReadOnlyList<YouTubeTrackItem>> SearchTracksAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return [];
        }

        var client = CurrentClient();
        var results = new List<YouTubeTrackItem>(SearchResultLimit);
        try
        {
            await foreach (var video in client.Search
                               .GetVideosAsync(trimmed, cancellationToken)
                               .ConfigureAwait(false))
            {
                var id = video.Id.Value;
                var author = string.IsNullOrWhiteSpace(video.Author.ChannelTitle)
                    ? "YouTube"
                    : video.Author.ChannelTitle;

                results.Add(new YouTubeTrackItem(
                    id,
                    video.Title,
                    author,
                    video.Duration,
                    GetThumbnailUrl(video.Thumbnails, id)));

                if (results.Count >= SearchResultLimit)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new YouTubeServiceException(
                ClassifyException(exception, YouTubeFailureKind.SearchFailed),
                "YouTube search failed.",
                exception);
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
            throw new ArgumentException("The value is not a YouTube video id.", nameof(videoId));
        }

        if (!string.Equals(Path.GetExtension(outputFilePath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("YouTube ingestion must produce a .wav file.", nameof(outputFilePath));
        }

        var fullOutputPath = Path.GetFullPath(outputFilePath);
        var directory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException("The output path has no directory.", nameof(outputFilePath));
        Directory.CreateDirectory(directory);

        var runId = Guid.NewGuid().ToString("N");
        var temporaryDownloadPath = Path.Combine(directory, ".youtube-" + runId + ".download");
        var temporaryWavePath = Path.Combine(directory, ".youtube-" + runId + ".wav");
        var reporter = progress is null ? null : new MonotonicProgress(progress);

        try
        {
            await EnsureFfmpegAvailableAsync(cancellationToken).ConfigureAwait(false);
            reporter?.Report(0.01);

            var client = CurrentClient();
            var streamInfo = await ResolveAudioStreamAsync(client, videoId, cancellationToken)
                .ConfigureAwait(false);
            reporter?.Report(0.03);

            await client.Videos.Streams.DownloadAsync(
                streamInfo,
                temporaryDownloadPath,
                new ScaledProgress(reporter, 0.03, 0.82),
                cancellationToken).ConfigureAwait(false);

            var downloaded = new FileInfo(temporaryDownloadPath);
            if (!downloaded.Exists || downloaded.Length == 0)
            {
                throw new YouTubeServiceException(
                    YouTubeFailureKind.DownloadFailed,
                    "YouTube returned an empty audio stream.");
            }

            reporter?.Report(0.86);
            await ConvertToWaveAsync(temporaryDownloadPath, temporaryWavePath, cancellationToken)
                .ConfigureAwait(false);

            File.Move(temporaryWavePath, fullOutputPath, overwrite: true);
            if (!File.Exists(fullOutputPath))
            {
                throw new YouTubeServiceException(
                    YouTubeFailureKind.ConversionFailed,
                    "The converted YouTube audio file could not be published.");
            }

            reporter?.Report(1);
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
            var kind = ClassifyException(exception, YouTubeFailureKind.DownloadFailed);
            throw new YouTubeServiceException(
                kind,
                kind == YouTubeFailureKind.DownloadFailed
                    ? "YouTube audio could not be downloaded or converted."
                    : exception.Message,
                exception);
        }
        finally
        {
            TryDelete(temporaryDownloadPath);
            TryDelete(temporaryWavePath);
        }
    }

    public void Dispose()
    {
        YoutubeClient client;
        lock (_clientLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            client = _client;
        }

        client.Dispose();
    }

    /// <summary>
    /// Берёт лучший поток «только аудио»; если YouTube их не отдал, берёт
    /// смешанный поток, из которого FFmpeg всё равно извлечёт звук.
    /// </summary>
    private static async Task<IStreamInfo> ResolveAudioStreamAsync(
        YoutubeClient client,
        string videoId,
        CancellationToken cancellationToken)
    {
        var manifest = await client.Videos.Streams
            .GetManifestAsync(videoId, cancellationToken)
            .ConfigureAwait(false);

        IStreamInfo? best = manifest.GetAudioOnlyStreams()
            .OrderByDescending(static stream => stream.Bitrate.BitsPerSecond)
            .FirstOrDefault();
        best ??= manifest.GetMuxedStreams()
            .OrderByDescending(static stream => stream.Bitrate.BitsPerSecond)
            .FirstOrDefault();

        return best ?? throw new YouTubeServiceException(
            YouTubeFailureKind.AudioStreamUnavailable,
            "YouTube did not provide an audio stream for this video.");
    }

    private YoutubeClient CurrentClient()
    {
        lock (_clientLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client;
        }
    }

    private void ReplaceClient(IReadOnlyList<Cookie> cookies, string? cookieFilePath)
    {
        var next = _clientFactory(cookies);
        YoutubeClient previous;
        lock (_clientLock)
        {
            previous = _client;
            _client = next;
            _cookieFilePath = cookieFilePath;
        }

        previous.Dispose();
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
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"FFmpeg could not convert the YouTube audio stream (exit code {result.ExitCode})."),
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

    /// <summary>
    /// Относит исключение YoutubeExplode к категории для локализованного
    /// совета UI. Неизвестные сбои остаются общей ошибкой поиска или загрузки.
    /// </summary>
    internal static YouTubeFailureKind ClassifyException(
        Exception exception,
        YouTubeFailureKind fallback) =>
        exception switch
        {
            VideoRequiresPurchaseException => YouTubeFailureKind.VideoUnplayable,
            VideoUnavailableException => YouTubeFailureKind.VideoUnavailable,
            VideoUnplayableException unplayable when LooksLikeSignIn(unplayable.Message) =>
                YouTubeFailureKind.SignInRequired,
            VideoUnplayableException => YouTubeFailureKind.VideoUnplayable,
            RequestLimitExceededException => YouTubeFailureKind.SignInRequired,
            _ when LooksLikeSignIn(exception.Message) => YouTubeFailureKind.SignInRequired,
            _ => fallback,
        };

    private static bool LooksLikeSignIn(string? message)
    {
        var text = message ?? string.Empty;
        return text.Contains("Sign in", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not a bot", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("confirm your age", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Разбирает cookies.txt в формате Netscape. Оставляет только cookies
    /// youtube.com и google.com: остальное YouTube не нужно.
    /// </summary>
    internal static IReadOnlyList<Cookie> ParseNetscapeCookies(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        const string httpOnlyPrefix = "#HttpOnly_";
        var cookies = new List<Cookie>();
        foreach (var rawLine in lines)
        {
            var line = (rawLine ?? string.Empty).TrimEnd('\r', '\n');
            if (line.Length == 0)
            {
                continue;
            }

            var httpOnly = false;
            if (line.StartsWith(httpOnlyPrefix, StringComparison.Ordinal))
            {
                line = line[httpOnlyPrefix.Length..];
                httpOnly = true;
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 7)
            {
                continue;
            }

            var domain = parts[0].Trim();
            var name = parts[5].Trim();
            if (name.Length == 0 || !IsYouTubeDomain(domain))
            {
                continue;
            }

            try
            {
                var cookie = new Cookie(
                    name,
                    parts[6].Trim(),
                    string.IsNullOrWhiteSpace(parts[2]) ? "/" : parts[2].Trim(),
                    domain)
                {
                    Secure = string.Equals(parts[3].Trim(), "TRUE", StringComparison.OrdinalIgnoreCase),
                    HttpOnly = httpOnly,
                };

                if (long.TryParse(
                        parts[4].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var expires) &&
                    expires > 0 &&
                    expires < 253_402_300_799)
                {
                    cookie.Expires = DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime;
                }

                cookies.Add(cookie);
            }
            catch (CookieException)
            {
            }
        }

        return cookies;
    }

    private static bool IsYouTubeDomain(string domain)
    {
        var host = domain.TrimStart('.');
        return MatchesDomain(host, "youtube.com") || MatchesDomain(host, "google.com");
    }

    private static bool MatchesDomain(string host, string root) =>
        string.Equals(host, root, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Id видео проверяется до любых запросов: чужая строка не должна
    /// превратиться в произвольный URL.
    /// </summary>
    internal static bool IsValidVideoId(string videoId) =>
        videoId is not null && VideoIdRegex().IsMatch(videoId);

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    private static string GetThumbnailUrl(IReadOnlyList<Thumbnail> thumbnails, string videoId) =>
        thumbnails
            .Where(static thumbnail => !string.IsNullOrWhiteSpace(thumbnail.Url))
            .OrderByDescending(static thumbnail =>
                (long)thumbnail.Resolution.Width * thumbnail.Resolution.Height)
            .Select(static thumbnail => thumbnail.Url)
            .FirstOrDefault()
        ?? "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg";

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
    /// Переводит прогресс загрузки (0..1) в его долю общего прогресса
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
    /// Не даёт индикатору откатиться назад.
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

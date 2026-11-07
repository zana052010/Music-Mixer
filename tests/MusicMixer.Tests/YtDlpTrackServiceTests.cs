// Тесты YtDlpTrackService: разбор NDJSON поиска, прогресса загрузки,
// классификация сбоев yt-dlp и оркестрация скачивания на подставном раннере.
using MusicMixer.Application.YouTube;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processes;
using MusicMixer.Infrastructure.YouTube;

namespace MusicMixer.Tests;

public sealed class YtDlpTrackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MusicMixer.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("dQw4w9WgXc-", true)]
    // Реальный id из поиска может начинаться с дефиса; он всегда подставляется
    // внутрь URL, поэтому допустимый набор символов остаётся безопасным.
    [InlineData("-x-x21HeWdk", true)]
    [InlineData("dQw4w9WgXc", false)]
    [InlineData("dQw4w9WgXcQX", false)]
    [InlineData("../escape", false)]
    [InlineData("", false)]
    public void IsValidVideoId_AcceptsOnlyElevenCharacterIds(string videoId, bool expected) =>
        Assert.Equal(expected, YtDlpTrackService.IsValidVideoId(videoId));

    [Fact]
    public void TryParseSearchEntry_MapsFieldsAndLargestThumbnail()
    {
        const string line = """
            {"_type":"url","id":"abc123Def45","title":"Тест: видео","duration":281,
             "uploader":"Канал загрузчик","channel":"Канал",
             "thumbnails":[{"url":"https://i.ytimg.com/small.jpg","width":360,"height":202},
                           {"url":"https://i.ytimg.com/big.jpg","width":720,"height":404}]}
            """;

        var parsed = YtDlpTrackService.TryParseSearchEntry(line, out var item);

        Assert.True(parsed);
        Assert.NotNull(item);
        Assert.Equal("abc123Def45", item!.Id);
        Assert.Equal("Тест: видео", item.Title);
        Assert.Equal("Канал", item.Author);
        Assert.Equal(TimeSpan.FromSeconds(281), item.Duration);
        Assert.Equal("https://i.ytimg.com/big.jpg", item.ThumbnailUrl);
    }

    [Fact]
    public void TryParseSearchEntry_FallsBackWhenFieldsMissing()
    {
        const string line = """{"id":"abc123Def45","title":"Only title"}""";

        var parsed = YtDlpTrackService.TryParseSearchEntry(line, out var item);

        Assert.True(parsed);
        Assert.NotNull(item);
        Assert.Equal("YouTube", item!.Author);
        Assert.Null(item.Duration);
        Assert.Equal($"https://i.ytimg.com/vi/abc123Def45/mqdefault.jpg", item.ThumbnailUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[download] Getting video 1 of 15")]
    [InlineData("{\"no title here\":true}")]
    [InlineData("{not json")]
    public void TryParseSearchEntry_SkipsUnparseableLines(string line)
    {
        var parsed = YtDlpTrackService.TryParseSearchEntry(line, out var item);

        Assert.False(parsed);
        Assert.Null(item);
    }

    [Fact]
    public void TryParseProgressLine_ComputesFractionFromTotalOrEstimate()
    {
        Assert.True(YtDlpTrackService.TryParseProgressLine(
            "download:@@250@@1000@@900", out var fraction));
        Assert.Equal(0.25, fraction, 6);

        // total_bytes отсутствует (NA): yt-dlp отдаёт только оценку.
        Assert.True(YtDlpTrackService.TryParseProgressLine(
            "download:@@450@@NA@@900", out fraction));
        Assert.Equal(0.5, fraction, 6);

        Assert.False(YtDlpTrackService.TryParseProgressLine(
            "download:@@450@@NA@@NA", out _));
        Assert.False(YtDlpTrackService.TryParseProgressLine(
            "[download]   42.0% of 10.00MiB", out _));
        Assert.False(YtDlpTrackService.TryParseProgressLine("", out _));
    }

    [Theory]
    [InlineData(
        "ERROR: [youtube] abc123Def45: Video unavailable. This video is not available",
        YouTubeFailureKind.VideoUnavailable)]
    [InlineData("ERROR: Private video. Sign in if you've been granted access",
        YouTubeFailureKind.VideoUnavailable)]
    [InlineData("ERROR: [youtube] abc123Def45: Sign in to confirm you're not a bot",
        YouTubeFailureKind.SignInRequired)]
    [InlineData("ERROR: [youtube] abc123Def45: This video is age-restricted",
        YouTubeFailureKind.VideoUnplayable)]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden",
        YouTubeFailureKind.DownloadFailed)]
    public void ClassifyFailureOutput_MapsKnownYtDlpErrors(string tail, YouTubeFailureKind expected) =>
        Assert.Equal(expected, YtDlpTrackService.ClassifyFailureOutput(tail));

    [Fact]
    public async Task SearchTracksAsync_ParsesNdjsonLinesAndBuildsYtSearchQuery()
    {
        var runner = new FakeProcessRunner
        {
            SearchLines =
            [
                """{"id":"abc123Def45","title":"Первое видео","duration":120}""",
                "[download] Getting video 2 of 15",
                """{"id":"xyz987Abc12","title":"Второе видео"}""",
            ],
        };
        var service = new YtDlpTrackService(runner, new FfmpegOptions(), new YtDlpOptions());

        var results = await service.SearchTracksAsync("  face  ");

        Assert.Equal(2, results.Count);
        Assert.Equal("abc123Def45", results[0].Id);
        Assert.Equal("Второе видео", results[1].Title);
        var search = runner.Requests.Single(request =>
            request.ExecutablePath.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ytsearch15:face", search.Arguments);
    }

    [Fact]
    public async Task SearchTracksAsync_FailsWithSearchKindOnNonZeroExit()
    {
        var runner = new FakeProcessRunner { ExitCode = 1 };
        var service = new YtDlpTrackService(runner, new FfmpegOptions(), new YtDlpOptions());

        var exception = await Assert.ThrowsAsync<YouTubeServiceException>(
            () => service.SearchTracksAsync("face"));

        Assert.Equal(YouTubeFailureKind.SearchFailed, exception.Kind);
    }

    [Fact]
    public async Task DownloadAudioStreamAsync_PublishesWaveAndReportsProgress()
    {
        Directory.CreateDirectory(_root);
        var outputPath = Path.Combine(_root, "video.wav");
        var progress = new CollectingProgress();
        var runner = new FakeProcessRunner();
        var service = new YtDlpTrackService(runner, new FfmpegOptions(), new YtDlpOptions());

        await service.DownloadAudioStreamAsync("dQw4w9WgXcQ", outputPath, progress);

        Assert.True(File.Exists(outputPath));
        Assert.Equal(1.0, progress.Values[^1], 6);
        // Прогресс идёт от проверки FFmpeg к публикации: дубликатов и регрессий нет.
        Assert.True(progress.Values.Zip(progress.Values.Skip(1))
            .All(pair => pair.Second >= pair.First));
        // Временные файлы удалены, остался только опубликованный WAV.
        Assert.Equal(
            [Path.GetFileName(outputPath)],
            Directory.GetFiles(_root)
                .Select(static path => Path.GetFileName(path)!)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray());

        var download = runner.Requests.Single(request =>
            request.ExecutablePath.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("https://www.youtube.com/watch?v=dQw4w9WgXcQ", download.Arguments);
        Assert.Equal(
            ["-f", "bestaudio/best"],
            download.Arguments.Zip(download.Arguments.Skip(1))
                .Where(pair => pair.First == "-f")
                .Select(pair => new[] { pair.First, pair.Second })
                .Single());
    }

    [Fact]
    public async Task DownloadAudioStreamAsync_MapsYtDlpFailureToLocalKind()
    {
        Directory.CreateDirectory(_root);
        var runner = new FakeProcessRunner
        {
            ExitCode = 1,
            OutputTail = "ERROR: [youtube] dQw4w9WgXcQ: Video unavailable",
        };
        var service = new YtDlpTrackService(runner, new FfmpegOptions(), new YtDlpOptions());

        var exception = await Assert.ThrowsAsync<YouTubeServiceException>(
            () => service.DownloadAudioStreamAsync(
                "dQw4w9WgXcQ",
                Path.Combine(_root, "video.wav")));

        Assert.Equal(YouTubeFailureKind.VideoUnavailable, exception.Kind);
    }

    [Fact]
    public async Task DownloadAudioStreamAsync_FailsFastWhenFfmpegUnavailable()
    {
        Directory.CreateDirectory(_root);
        var runner = new FakeProcessRunner
        {
            FailFfmpegVersionCheck = true,
            OutputTail = "ffmpeg: command not found",
        };
        var service = new YtDlpTrackService(runner, new FfmpegOptions(), new YtDlpOptions());

        await Assert.ThrowsAsync<ToolUnavailableException>(
            () => service.DownloadAudioStreamAsync(
                "dQw4w9WgXcQ",
                Path.Combine(_root, "video.wav")));

        // yt-dlp не должен запускаться, если конвертер заранее недоступен.
        Assert.DoesNotContain(
            runner.Requests,
            request => request.ExecutablePath.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DownloadAudioStreamAsync_RejectsNonVideoIdBeforeAnyProcess()
    {
        var runner = new FakeProcessRunner();
        var service = new YtDlpTrackService(runner, new FfmpegOptions(), new YtDlpOptions());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.DownloadAudioStreamAsync(
                "--exec-danger",
                Path.Combine(_root, "video.wav")));

        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task SearchTracksAsync_PassesCookiesWhenConfigured()
    {
        var runner = new FakeProcessRunner();
        var options = new YtDlpOptions
        {
            ExecutablePath = "yt-dlp",
            CookiesFile = @"C:\cookies.txt",
            CookiesFromBrowser = "firefox",
        };
        var service = new YtDlpTrackService(runner, new FfmpegOptions(), options);

        await service.SearchTracksAsync("face");

        var arguments = runner.Requests.Single().Arguments;
        Assert.Equal(@"C:\cookies.txt", FindArgumentValue(arguments, "--cookies"));
        Assert.Equal("firefox", FindArgumentValue(arguments, "--cookies-from-browser"));
    }

    [Fact]
    public async Task DownloadAudioStreamAsync_RetriesWithBrowserCookiesOnBotCheck()
    {
        Directory.CreateDirectory(_root);
        var outputPath = Path.Combine(_root, "video.wav");
        var runner = new FakeProcessRunner { FailFirstDownloadWithBotCheck = true };
        var service = new YtDlpTrackService(
            runner,
            new FfmpegOptions(),
            new YtDlpOptions { ExecutablePath = "yt-dlp" },
            defaultBrowserProvider: () => "edge");

        await service.DownloadAudioStreamAsync("dQw4w9WgXcQ", outputPath);

        var downloads = runner.Requests
            .Where(request => request.ExecutablePath.Contains(
                "yt-dlp",
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(2, downloads.Count);
        Assert.Null(FindArgumentValue(downloads[0].Arguments, "--cookies-from-browser"));
        Assert.Equal(
            "edge",
            FindArgumentValue(downloads[1].Arguments, "--cookies-from-browser"));
        Assert.Contains(
            "youtube:player_client=tv,web_safari",
            downloads[0].Arguments);
        Assert.DoesNotContain(
            "youtube:player_client=tv,web_safari",
            downloads[1].Arguments);
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task DownloadAudioStreamAsync_ReportsSignInKindWhenCookieRetryFails()
    {
        Directory.CreateDirectory(_root);
        var runner = new FakeProcessRunner { AlwaysFailDownloadWithBotCheck = true };
        var service = new YtDlpTrackService(
            runner,
            new FfmpegOptions(),
            new YtDlpOptions { ExecutablePath = "yt-dlp" },
            defaultBrowserProvider: () => "edge");

        var exception = await Assert.ThrowsAsync<YouTubeServiceException>(
            () => service.DownloadAudioStreamAsync(
                "dQw4w9WgXcQ",
                Path.Combine(_root, "video.wav")));

        Assert.Equal(YouTubeFailureKind.SignInRequired, exception.Kind);
        Assert.Equal(
            2,
            runner.Requests.Count(request => request.ExecutablePath.Contains(
                "yt-dlp",
                StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task SearchTracksAsync_PassesSelectedCookieFile()
    {
        Directory.CreateDirectory(_root);
        var cookiesPath = Path.Combine(_root, "cookies.txt");
        await File.WriteAllTextAsync(cookiesPath, "# Netscape HTTP Cookie File");
        var runner = new FakeProcessRunner();
        var service = new YtDlpTrackService(
            runner,
            new FfmpegOptions(),
            new YtDlpOptions { ExecutablePath = "yt-dlp" });

        service.SetCookieFile(cookiesPath);
        await service.SearchTracksAsync("face");

        Assert.True(service.SupportsCookieFile);
        Assert.Equal(cookiesPath, service.CookieFilePath);
        Assert.Equal(
            cookiesPath,
            FindArgumentValue(runner.Requests.Single().Arguments, "--cookies"));
    }

    [Fact]
    public void SetCookieFile_RejectsMissingFile()
    {
        var service = new YtDlpTrackService(
            new FakeProcessRunner(),
            new FfmpegOptions(),
            new YtDlpOptions { ExecutablePath = "yt-dlp" });

        Assert.Throws<FileNotFoundException>(
            () => service.SetCookieFile(Path.Combine(_root, "missing.txt")));
    }

    [Fact]
    public void FromEnvironment_ReadsCookieOverrides()
    {
        Environment.SetEnvironmentVariable("MUSICMIXER_YTDLP_COOKIES", @"C:\cookies.txt");
        Environment.SetEnvironmentVariable("MUSICMIXER_YTDLP_COOKIES_FROM_BROWSER", "chrome");
        Environment.SetEnvironmentVariable("MUSICMIXER_YTDLP_AUTO_COOKIES", "0");
        Environment.SetEnvironmentVariable("MUSICMIXER_YTDLP_JS_RUNTIME", "node");
        try
        {
            var options = YtDlpOptions.FromEnvironment();

            Assert.Equal(@"C:\cookies.txt", options.CookiesFile);
            Assert.Equal("chrome", options.CookiesFromBrowser);
            Assert.False(options.AutoCookiesFromBrowser);
            Assert.Equal("node", options.JavaScriptRuntime);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MUSICMIXER_YTDLP_COOKIES", null);
            Environment.SetEnvironmentVariable("MUSICMIXER_YTDLP_COOKIES_FROM_BROWSER", null);
            Environment.SetEnvironmentVariable("MUSICMIXER_YTDLP_AUTO_COOKIES", null);
            Environment.SetEnvironmentVariable("MUSICMIXER_YTDLP_JS_RUNTIME", null);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string? FindArgumentValue(
        IReadOnlyList<string> arguments,
        string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private sealed class CollectingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    /// <summary>
    /// Подставной раннер: проверка FFmpeg, yt-dlp и конвертация имитируются
    /// без реальных бинарников, как в DemucsCliSeparatorServiceTests.
    /// </summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<ProcessLaunchRequest> Requests { get; } = [];

        /// <summary>Строки NDJSON, которые yt-dlp печатает в stdout при поиске.</summary>
        public IReadOnlyList<string> SearchLines { get; init; } = [];

        public int ExitCode { get; init; }

        public string OutputTail { get; init; } = string.Empty;

        public bool FailFfmpegVersionCheck { get; init; }

        public bool FailFirstDownloadWithBotCheck { get; init; }

        public bool AlwaysFailDownloadWithBotCheck { get; init; }

        private int _downloadAttempts;

        public Task<ProcessExecutionResult> RunAsync(
            ProcessLaunchRequest request,
            Action<ProcessOutputLine>? onOutputLine = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();

            var arguments = request.Arguments;
            if (arguments.Contains("-version"))
            {
                return Task.FromResult(FailFfmpegVersionCheck
                    ? new ProcessExecutionResult(1, OutputTail)
                    : new ProcessExecutionResult(0, "ffmpeg version 7.0"));
            }

            if (request.ExecutablePath.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase))
            {
                var downloadPath = FindArgumentValue(arguments, "-o");
                if (downloadPath is null)
                {
                    // Поиск: NDJSON-строки в stdout, файлы не создаются.
                    foreach (var line in SearchLines)
                    {
                        onOutputLine?.Invoke(new ProcessOutputLine(line, IsError: false));
                    }

                    return Task.FromResult(new ProcessExecutionResult(ExitCode, OutputTail));
                }

                if (ExitCode == 0)
                {
                    _downloadAttempts++;
                    if (AlwaysFailDownloadWithBotCheck ||
                        (FailFirstDownloadWithBotCheck && _downloadAttempts == 1))
                    {
                        return Task.FromResult(new ProcessExecutionResult(
                            1,
                            "ERROR: [youtube] dQw4w9WgXcQ: Sign in to confirm you're not a bot"));
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);
                    File.WriteAllBytes(downloadPath, [0x49, 0x44, 0x33]);
                    onOutputLine?.Invoke(new ProcessOutputLine(
                        "download:@@512@@2048@@NA",
                        IsError: false));
                }

                return Task.FromResult(new ProcessExecutionResult(ExitCode, OutputTail));
            }

            // Конвертация FFmpeg: путь вывода — последний аргумент командной строки.
            var outputPath = arguments[^1];
            File.WriteAllBytes(outputPath, [0x52, 0x49, 0x46, 0x46]);
            return Task.FromResult(new ProcessExecutionResult(ExitCode, OutputTail));
        }
    }
}

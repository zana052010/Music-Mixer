// Тесты YoutubeExplodeTrackService без сети: валидация id, разбор cookies.txt,
// классификация ошибок и отказ до скачивания, если FFmpeg недоступен.
using MusicMixer.Application.YouTube;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processes;
using MusicMixer.Infrastructure.YouTube;

namespace MusicMixer.Tests;

public sealed class YoutubeExplodeTrackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MusicMixer.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("dQw4w9WgXc-", true)]
    [InlineData("-x-x21HeWdk", true)]
    [InlineData("dQw4w9WgXc", false)]
    [InlineData("dQw4w9WgXcQX", false)]
    [InlineData("../escape", false)]
    [InlineData("", false)]
    public void IsValidVideoId_AcceptsOnlyElevenCharacterIds(string videoId, bool expected) =>
        Assert.Equal(expected, YoutubeExplodeTrackService.IsValidVideoId(videoId));

    [Fact]
    public void ParseNetscapeCookies_KeepsOnlyYouTubeAndGoogleCookies()
    {
        string[] lines =
        [
            "# Netscape HTTP Cookie File",
            "",
            ".youtube.com\tTRUE\t/\tTRUE\t1893456000\tPREF\tf6",
            "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1893456000\tLOGIN_INFO\tabc",
            ".google.com\tTRUE\t/\tFALSE\t0\tSID\tsecret",
            ".example.com\tTRUE\t/\tFALSE\t1893456000\tOTHER\tvalue",
            "broken line",
        ];

        var cookies = YoutubeExplodeTrackService.ParseNetscapeCookies(lines);

        Assert.Equal(["PREF", "LOGIN_INFO", "SID"], cookies.Select(static cookie => cookie.Name).ToArray());
        Assert.True(cookies[0].Secure);
        Assert.True(cookies[1].HttpOnly);
        Assert.False(cookies[2].Secure);
    }

    [Fact]
    public void SetCookieFile_RemembersFullPathAndCanBeCleared()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "cookies.txt");
        File.WriteAllLines(path, [".youtube.com\tTRUE\t/\tTRUE\t1893456000\tPREF\tf6"]);
        using var service = CreateService(new FakeProcessRunner());

        service.SetCookieFile(path);
        Assert.Equal(Path.GetFullPath(path), service.CookieFilePath);

        service.SetCookieFile(null);
        Assert.Null(service.CookieFilePath);
    }

    [Fact]
    public void SetCookieFile_RejectsFileWithoutYouTubeCookies()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "cookies.txt");
        File.WriteAllLines(path, [".example.com\tTRUE\t/\tFALSE\t1893456000\tOTHER\tvalue"]);
        using var service = CreateService(new FakeProcessRunner());

        Assert.Throws<InvalidDataException>(() => service.SetCookieFile(path));
        Assert.Null(service.CookieFilePath);
    }

    [Fact]
    public void SetCookieFile_RejectsMissingFile()
    {
        using var service = CreateService(new FakeProcessRunner());

        Assert.Throws<FileNotFoundException>(
            () => service.SetCookieFile(Path.Combine(_root, "missing.txt")));
    }

    [Fact]
    public async Task SearchTracksAsync_ReturnsEmptyListForBlankQuery()
    {
        using var service = CreateService(new FakeProcessRunner());

        var results = await service.SearchTracksAsync("   ");

        Assert.Empty(results);
    }

    [Fact]
    public async Task DownloadAudioStreamAsync_RejectsNonVideoIdBeforeAnyProcess()
    {
        var runner = new FakeProcessRunner();
        using var service = CreateService(runner);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.DownloadAudioStreamAsync(
                "--exec-danger",
                Path.Combine(_root, "video.wav")));

        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task DownloadAudioStreamAsync_RejectsNonWaveOutput()
    {
        var runner = new FakeProcessRunner();
        using var service = CreateService(runner);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.DownloadAudioStreamAsync(
                "dQw4w9WgXcQ",
                Path.Combine(_root, "video.mp3")));

        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task DownloadAudioStreamAsync_FailsFastWhenFfmpegUnavailable()
    {
        Directory.CreateDirectory(_root);
        var runner = new FakeProcessRunner { FailFfmpegVersionCheck = true };
        using var service = CreateService(runner);

        await Assert.ThrowsAsync<ToolUnavailableException>(
            () => service.DownloadAudioStreamAsync(
                "dQw4w9WgXcQ",
                Path.Combine(_root, "video.wav")));

        // До YouTube дело не дошло, временных файлов не осталось.
        Assert.Single(runner.Requests);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("Sign in to confirm you're not a bot", YouTubeFailureKind.SignInRequired)]
    [InlineData("Please confirm your age", YouTubeFailureKind.SignInRequired)]
    [InlineData("Connection reset by peer", YouTubeFailureKind.DownloadFailed)]
    public void ClassifyException_DetectsSignInMessages(string message, YouTubeFailureKind expected) =>
        Assert.Equal(
            expected,
            YoutubeExplodeTrackService.ClassifyException(
                new HttpRequestException(message),
                YouTubeFailureKind.DownloadFailed));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static YoutubeExplodeTrackService CreateService(FakeProcessRunner runner) =>
        new(runner, new FfmpegOptions(), new YouTubeOptions());

    /// <summary>
    /// Подставной раннер: имитирует только проверку FFmpeg, сеть не нужна.
    /// </summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<ProcessLaunchRequest> Requests { get; } = [];

        public bool FailFfmpegVersionCheck { get; init; }

        public Task<ProcessExecutionResult> RunAsync(
            ProcessLaunchRequest request,
            Action<ProcessOutputLine>? onOutputLine = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(FailFfmpegVersionCheck
                ? new ProcessExecutionResult(1, "ffmpeg: command not found")
                : new ProcessExecutionResult(0, "ffmpeg version 7.0"));
        }
    }
}

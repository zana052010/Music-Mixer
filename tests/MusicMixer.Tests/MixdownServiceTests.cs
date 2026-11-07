// Тесты сервиса сведения: проверяют фильтр FFmpeg и публикацию результата
// без запуска реального инструмента.
using MusicMixer.Domain.Exceptions;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processing;
using MusicMixer.Infrastructure.Processes;

namespace MusicMixer.Tests;

public sealed class MixdownServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MusicMixer.MixdownTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExportAsync_PositiveOffset_UsesDelayAndPublishesOutput()
    {
        var (instrumental, vocal, output) = CreateInputs();
        var runner = new CapturingProcessRunner();

        var service = new FfmpegMixdownService(runner, new FfmpegOptions());
        var result = await service.ExportAsync(
            instrumental,
            vocal,
            TimeSpan.FromSeconds(1.5),
            instrumentalVolume: 0.8,
            vocalVolume: 0.6,
            expectedDuration: TimeSpan.FromSeconds(10),
            output);

        var filter = GetFilter(runner);
        Assert.Contains("volume=0.8", filter);
        Assert.Contains("volume=0.6", filter);
        Assert.Contains("adelay=delays=1500:all=1", filter);
        Assert.Contains("amix=inputs=2:duration=first", filter);
        Assert.Contains("normalize=0", filter);
        Assert.Contains("alimiter=limit=0.95", filter);
        Assert.Equal(Path.GetFullPath(output), result);
        Assert.True(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(_root, ".musicmixer-mix-*"));
    }

    [Fact]
    public async Task ExportAsync_NegativeOffset_TrimsVocalLeadIn()
    {
        var (instrumental, vocal, output) = CreateInputs();
        var runner = new CapturingProcessRunner();

        var service = new FfmpegMixdownService(runner, new FfmpegOptions());
        await service.ExportAsync(
            instrumental,
            vocal,
            TimeSpan.FromSeconds(-2),
            instrumentalVolume: 0.5,
            vocalVolume: 0.5,
            expectedDuration: TimeSpan.FromSeconds(10),
            output);

        var filter = GetFilter(runner);
        Assert.Contains("atrim=start=2", filter);
        Assert.Contains("asetpts=PTS-STARTPTS", filter);
        Assert.DoesNotContain("adelay", filter);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task ExportAsync_FfmpegFailure_ThrowsAndLeavesNoOutput()
    {
        var (instrumental, vocal, output) = CreateInputs();
        var runner = new CapturingProcessRunner
        {
            ExitCode = 1,
            OutputTail = "boom",
        };

        var service = new FfmpegMixdownService(runner, new FfmpegOptions());

        var exception = await Assert.ThrowsAsync<AudioProcessingException>(() =>
            service.ExportAsync(
                instrumental,
                vocal,
                TimeSpan.Zero,
                instrumentalVolume: 1,
                vocalVolume: 1,
                expectedDuration: TimeSpan.FromSeconds(10),
                output));

        Assert.Equal("boom", exception.Diagnostics);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(_root, ".musicmixer-mix-*"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private (string Instrumental, string Vocal, string Output) CreateInputs()
    {
        Directory.CreateDirectory(_root);
        var instrumental = Path.Combine(_root, "instrumental.wav");
        var vocal = Path.Combine(_root, "vocal.wav");
        File.WriteAllBytes(instrumental, [1, 2, 3, 4]);
        File.WriteAllBytes(vocal, [5, 6, 7, 8]);
        return (instrumental, vocal, Path.Combine(_root, "mix.wav"));
    }

    private static string GetFilter(CapturingProcessRunner runner)
    {
        var arguments = Assert.IsType<ProcessLaunchRequest>(runner.Request).Arguments;
        var index = arguments.ToList().IndexOf("-filter_complex");
        Assert.True(index >= 0, "FFmpeg arguments must contain -filter_complex.");
        return arguments[index + 1];
    }

    private sealed class CapturingProcessRunner : IProcessRunner
    {
        public ProcessLaunchRequest? Request { get; private set; }

        public int ExitCode { get; init; }

        public string OutputTail { get; init; } = string.Empty;

        public Task<ProcessExecutionResult> RunAsync(
            ProcessLaunchRequest request,
            Action<ProcessOutputLine>? onOutputLine = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;

            if (ExitCode == 0)
            {
                // FFmpeg пишет результат последним аргументом; имитируем
                // успешное создание временного файла.
                var outputPath = request.Arguments[^1];
                File.WriteAllBytes(outputPath, [9, 9, 9]);
            }

            return Task.FromResult(new ProcessExecutionResult(ExitCode, OutputTail));
        }
    }
}

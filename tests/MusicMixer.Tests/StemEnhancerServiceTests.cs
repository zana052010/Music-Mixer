// Тесты FFmpeg-улучшения стемов: проверяют цепочки фильтров и публикацию файла
// без запуска реального FFmpeg.
using MusicMixer.Application.Abstractions;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processing;
using MusicMixer.Infrastructure.Processes;

namespace MusicMixer.Tests;

public sealed class StemEnhancerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MusicMixer.StemEnhancerTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnhanceAsync_Vocal_UsesVocalChainAndPublishesOutput()
    {
        var (source, output) = CreateInputs();
        var runner = new CapturingProcessRunner();

        var service = new FfmpegStemEnhancerService(runner, new FfmpegOptions());
        var result = await service.EnhanceAsync(
            source,
            output,
            AudioStemKind.Vocal,
            TimeSpan.FromSeconds(10));

        var filter = GetFilter(runner);
        Assert.Contains("highpass=f=70", filter);
        Assert.Contains("equalizer=f=3200", filter);
        Assert.Contains("acompressor", filter);
        Assert.Contains("alimiter=limit=0.95", filter);
        Assert.Equal(Path.GetFullPath(output), result);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task EnhanceAsync_Instrumental_UsesInstrumentalChain()
    {
        var (source, output) = CreateInputs();
        var runner = new CapturingProcessRunner();

        var service = new FfmpegStemEnhancerService(runner, new FfmpegOptions());
        await service.EnhanceAsync(
            source,
            output,
            AudioStemKind.Instrumental,
            TimeSpan.FromSeconds(10));

        var filter = GetFilter(runner);
        Assert.Contains("highpass=f=30", filter);
        Assert.Contains("equalizer=f=250", filter);
        Assert.Contains("equalizer=f=3000", filter);
        Assert.Contains("acompressor", filter);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task EnhanceAsync_FfmpegFailure_ThrowsAndLeavesNoOutput()
    {
        var (source, output) = CreateInputs();
        var runner = new CapturingProcessRunner
        {
            ExitCode = 1,
            OutputTail = "boom",
        };

        var service = new FfmpegStemEnhancerService(runner, new FfmpegOptions());

        var exception = await Assert.ThrowsAsync<AudioProcessingException>(() =>
            service.EnhanceAsync(
                source,
                output,
                AudioStemKind.Vocal,
                TimeSpan.FromSeconds(10)));

        Assert.Equal("boom", exception.Diagnostics);
        Assert.False(File.Exists(output));
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

    private (string Source, string Output) CreateInputs()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "stem.wav");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        return (source, Path.Combine(_root, "stem-enhanced.wav"));
    }

    private static string GetFilter(CapturingProcessRunner runner)
    {
        var arguments = Assert.IsType<ProcessLaunchRequest>(runner.Request).Arguments;
        var index = arguments.ToList().IndexOf("-af");
        Assert.True(index >= 0, "FFmpeg arguments must contain -af.");
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
                var outputPath = request.Arguments[^1];
                File.WriteAllBytes(outputPath, [9, 9, 9]);
            }

            return Task.FromResult(new ProcessExecutionResult(ExitCode, OutputTail));
        }
    }
}

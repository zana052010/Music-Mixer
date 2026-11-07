using MusicMixer.Domain.Models;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processes;
using MusicMixer.Infrastructure.Separation;

namespace MusicMixer.Tests;

public sealed class DemucsCliSeparatorServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MusicMixer.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SeparateAsync_DiscoversStemsAndReportsMonotonicProgress()
    {
        Directory.CreateDirectory(_root);
        var inputPath = Path.Combine(_root, "input.wav");
        await File.WriteAllBytesAsync(inputPath, [0x52, 0x49, 0x46, 0x46]);
        var outputDirectory = Path.Combine(_root, "out");
        var progress = new CollectingProgress();
        var service = new DemucsCliSeparatorService(
            new FakeDemucsProcessRunner(),
            new DemucsOptions());

        var result = await service.SeparateAsync(
            AudioTrack.CreateUnanalyzed(inputPath),
            outputDirectory,
            progress);

        Assert.True(File.Exists(result.InstrumentalPath));
        Assert.True(File.Exists(result.VocalsPath));
        Assert.Equal("no_vocals", Path.GetFileNameWithoutExtension(result.InstrumentalPath));
        Assert.Equal("vocals", Path.GetFileNameWithoutExtension(result.VocalsPath));
        Assert.NotEmpty(progress.Values);
        Assert.True(progress.Values.Zip(progress.Values.Skip(1)).All(pair => pair.Second.Fraction >= pair.First.Fraction));
        Assert.Equal(1.0, progress.Values[^1].Fraction, 6);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CollectingProgress : IProgress<OperationProgress>
    {
        public List<OperationProgress> Values { get; } = [];

        public void Report(OperationProgress value) => Values.Add(value);
    }

    private sealed class FakeDemucsProcessRunner : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(
            ProcessLaunchRequest request,
            Action<ProcessOutputLine>? onOutputLine = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stemDirectory = Path.Combine(request.WorkingDirectory!, "htdemucs", "input");
            Directory.CreateDirectory(stemDirectory);
            File.WriteAllBytes(Path.Combine(stemDirectory, "vocals.wav"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(stemDirectory, "no_vocals.wav"), [5, 6, 7, 8]);

            onOutputLine?.Invoke(new ProcessOutputLine("Separating track", IsError: false));
            onOutputLine?.Invoke(new ProcessOutputLine("  0%|          | 0/100", IsError: true));
            onOutputLine?.Invoke(new ProcessOutputLine(" 42%|####      | 42/100", IsError: true));
            onOutputLine?.Invoke(new ProcessOutputLine("100%|##########| 100/100", IsError: true));

            return Task.FromResult(new ProcessExecutionResult(0, string.Empty));
        }
    }
}

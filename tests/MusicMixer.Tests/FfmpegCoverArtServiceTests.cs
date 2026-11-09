using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processes;
using MusicMixer.Infrastructure.Processing;

namespace MusicMixer.Tests;

public sealed class FfmpegCoverArtServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mm-cover-" + Guid.NewGuid().ToString("N"));

    public FfmpegCoverArtServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ExtractEmbeddedCoverAsync_ReturnsPathWhenFfmpegWritesImage()
    {
        var audio = Path.Combine(_directory, "song.mp3");
        await File.WriteAllBytesAsync(audio, new byte[] { 1, 2, 3 });
        var runner = new FakeRunner(exitCode: 0, writeOutput: true);
        var service = new FfmpegCoverArtService(runner, new FfmpegOptions { ExecutablePath = "ffmpeg" });

        var result = await service.ExtractEmbeddedCoverAsync(audio, Path.Combine(_directory, "cover.png"));

        Assert.NotNull(result);
        Assert.True(File.Exists(result));
        Assert.Contains("0:v:0", runner.LastArguments);
    }

    [Fact]
    public async Task ExtractEmbeddedCoverAsync_ReturnsNullWhenFileHasNoPicture()
    {
        var audio = Path.Combine(_directory, "song.wav");
        await File.WriteAllBytesAsync(audio, new byte[] { 1, 2, 3 });
        var service = new FfmpegCoverArtService(
            new FakeRunner(exitCode: 1, writeOutput: false),
            new FfmpegOptions { ExecutablePath = "ffmpeg" });
        var output = Path.Combine(_directory, "cover.png");

        var result = await service.ExtractEmbeddedCoverAsync(audio, output);

        Assert.Null(result);
        Assert.False(File.Exists(output));
    }

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly int _exitCode;
        private readonly bool _writeOutput;

        public FakeRunner(int exitCode, bool writeOutput)
        {
            _exitCode = exitCode;
            _writeOutput = writeOutput;
        }

        public IReadOnlyList<string> LastArguments { get; private set; } = Array.Empty<string>();

        public Task<ProcessExecutionResult> RunAsync(
            ProcessLaunchRequest request,
            Action<ProcessOutputLine>? onOutputLine = null,
            CancellationToken cancellationToken = default)
        {
            LastArguments = request.Arguments;
            if (_writeOutput)
            {
                File.WriteAllBytes(request.Arguments[request.Arguments.Count - 1], new byte[] { 137, 80, 78, 71 });
            }

            return Task.FromResult(new ProcessExecutionResult(_exitCode, string.Empty));
        }
    }
}

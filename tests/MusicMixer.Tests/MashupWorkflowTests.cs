using MusicMixer.Application.Abstractions;
using MusicMixer.Application.Mashup;
using MusicMixer.Domain.Models;

namespace MusicMixer.Tests;

public sealed class MashupWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MusicMixer.WorkflowTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PrepareAsync_RunsPipelineAndProducesRenderableSession()
    {
        var pipeline = await CreatePipelineAsync();
        var progress = new CollectingProgress();

        var result = await pipeline.Workflow.PrepareAsync(
            AudioTrack.CreateUnanalyzed(pipeline.TrackAPath),
            AudioTrack.CreateUnanalyzed(pipeline.TrackBPath),
            pipeline.WorkspaceDirectory,
            progress,
            enhanceStems: true);

        Assert.Equal(
            [
                "analyze:track-a.wav",
                "analyze:track-b.wav",
                "separate:track-a.wav",
                "separate:track-b.wav",
                "enhance:Instrumental:track-a-instrumental.wav",
                "enhance:Vocal:track-b-vocals.wav",
                "render",
            ],
            pipeline.Log);
        Assert.Equal(1.0, progress.Values[^1].Fraction, 6);
        Assert.True(File.Exists(result.Session.InstrumentalPath));
        Assert.True(File.Exists(result.Session.SourceVocalPath));
        Assert.True(File.Exists(result.Session.AlignedVocalPath));
        Assert.EndsWith("instrumental-enhanced.wav", result.Session.InstrumentalPath);
        Assert.EndsWith("vocals-enhanced.wav", result.Session.SourceVocalPath);
        Assert.True(result.Session.InstrumentalTrack.HasAnalysis);
        Assert.True(result.Session.VocalTrack.HasAnalysis);
        Assert.Equal(128d / 120d, result.Alignment.Parameters.PlaybackSpeedRatio, 6);
        Assert.Equal(128d / 120d, result.Alignment.AutomaticPlaybackSpeedRatio, 6);
        Assert.Equal(0, result.Alignment.AutomaticPitchShiftSemitones, 6);
        Assert.NotNull(pipeline.Processing.LastParameters);

        var tuned = await pipeline.Workflow.RenderAlignedVocalAsync(
            result.Session,
            finePitchSemitones: 1.5,
            offsetNudge: TimeSpan.FromMilliseconds(400),
            outputFilePath: Path.Combine(pipeline.WorkspaceDirectory, "aligned-vocals-tuned.wav"),
            playbackSpeedRatioOverride: 1.25);

        Assert.True(File.Exists(tuned.AlignedVocalPath));
        Assert.Equal(1.5, tuned.Alignment.Parameters.PitchShiftSemitones, 6);
        Assert.Equal(1.25, tuned.Alignment.Parameters.PlaybackSpeedRatio, 6);
        Assert.Equal(128d / 120d, tuned.Alignment.AutomaticPlaybackSpeedRatio, 6);
        Assert.NotNull(pipeline.Processing.LastParameters);
        Assert.Equal(1.5, pipeline.Processing.LastParameters.PitchShiftSemitones, 6);
        Assert.Equal(1.25, pipeline.Processing.LastParameters.PlaybackSpeedRatio, 6);
    }

    [Fact]
    public async Task PrepareAsync_WithoutEnhancement_UsesOriginalStems()
    {
        var pipeline = await CreatePipelineAsync();
        var progress = new CollectingProgress();

        var result = await pipeline.Workflow.PrepareAsync(
            AudioTrack.CreateUnanalyzed(pipeline.TrackAPath),
            AudioTrack.CreateUnanalyzed(pipeline.TrackBPath),
            pipeline.WorkspaceDirectory,
            progress,
            enhanceStems: false);

        Assert.DoesNotContain(pipeline.Log, entry => entry.StartsWith("enhance:", StringComparison.Ordinal));
        Assert.EndsWith("track-a-instrumental.wav", result.Session.InstrumentalPath);
        Assert.EndsWith("track-b-vocals.wav", result.Session.SourceVocalPath);
    }

    private async Task<Pipeline> CreatePipelineAsync()
    {
        Directory.CreateDirectory(_root);
        var trackAPath = Path.Combine(_root, "track-a.wav");
        var trackBPath = Path.Combine(_root, "track-b.wav");
        await File.WriteAllBytesAsync(trackAPath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(trackBPath, [5, 6, 7, 8]);

        var log = new List<string>();
        var analyses = new Dictionary<string, AudioAnalysisResult>
        {
            [trackAPath] = new AudioAnalysisResult(
                TimeSpan.FromMinutes(3),
                128,
                TimeSpan.FromMilliseconds(500),
                new MusicalKey(PitchClass.C, KeyMode.Major),
                0.9,
                0.8),
            [trackBPath] = new AudioAnalysisResult(
                TimeSpan.FromMinutes(3),
                120,
                TimeSpan.FromMilliseconds(750),
                new MusicalKey(PitchClass.A, KeyMode.Minor),
                0.9,
                0.8),
        };

        var processing = new FakeProcessing(log);
        var workflow = new MashupWorkflow(
            new FakeAnalyzer(analyses, log),
            new FakeSeparator(log),
            processing,
            new FakeStemEnhancer(log));
        var workspaceDirectory = Path.Combine(_root, "workspace");
        return new Pipeline(workflow, trackAPath, trackBPath, workspaceDirectory, log, processing);
    }

    [Fact]
    public async Task RenderAlignedVocalAsync_UsesManualAnalysisOverrides()
    {
        var pipeline = await CreatePipelineAsync();
        var prepared = await pipeline.Workflow.PrepareAsync(
            AudioTrack.CreateUnanalyzed(pipeline.TrackAPath),
            AudioTrack.CreateUnanalyzed(pipeline.TrackBPath),
            pipeline.WorkspaceDirectory,
            enhanceStems: false);

        // Анализ дал 128 и 120 BPM; пользователь исправляет темп вокала на 128.
        var session = prepared.Session.WithAnalysisOverrides(
            TrackAnalysisOverride.None,
            new TrackAnalysisOverride(beatsPerMinute: 128));

        var tuned = await pipeline.Workflow.RenderAlignedVocalAsync(
            session,
            finePitchSemitones: 0,
            offsetNudge: TimeSpan.Zero,
            outputFilePath: Path.Combine(pipeline.WorkspaceDirectory, "aligned-vocals-manual.wav"));

        Assert.Equal(1.0, tuned.Alignment.AutomaticPlaybackSpeedRatio, 6);
        Assert.NotNull(pipeline.Processing.LastParameters);
        Assert.Equal(1.0, pipeline.Processing.LastParameters.PlaybackSpeedRatio, 6);
        Assert.Equal(120d, session.VocalTrack.BeatsPerMinute!.Value, 6);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record Pipeline(
        MashupWorkflow Workflow,
        string TrackAPath,
        string TrackBPath,
        string WorkspaceDirectory,
        List<string> Log,
        FakeProcessing Processing);

    private sealed class FakeStemEnhancer(List<string> log) : IAudioStemEnhancerService
    {
        public Task<string> EnhanceAsync(
            string sourceFilePath,
            string outputFilePath,
            AudioStemKind kind,
            TimeSpan sourceDuration,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log.Add($"enhance:{kind}:{Path.GetFileName(sourceFilePath)}");
            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);
            File.WriteAllBytes(outputFilePath, [7, 7, 7, 7]);
            progress?.Report(new OperationProgress(1, "fake enhancement complete"));
            return Task.FromResult(outputFilePath);
        }
    }

    private sealed class FakeAnalyzer(
        IReadOnlyDictionary<string, AudioAnalysisResult> analyses,
        List<string> log) : IAudioAnalyzerService
    {
        public Task<AudioAnalysisResult> AnalyzeAsync(
            string filePath,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log.Add($"analyze:{Path.GetFileName(filePath)}");
            progress?.Report(new OperationProgress(1, "fake analysis complete"));
            return Task.FromResult(analyses[filePath]);
        }
    }

    private sealed class FakeSeparator(List<string> log) : IAudioSeparatorService
    {
        public Task<StemSeparationResult> SeparateAsync(
            AudioTrack source,
            string outputDirectory,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log.Add($"separate:{Path.GetFileName(source.FilePath)}");
            Directory.CreateDirectory(outputDirectory);
            var name = Path.GetFileNameWithoutExtension(source.FilePath);
            var instrumental = Path.Combine(outputDirectory, $"{name}-instrumental.wav");
            var vocals = Path.Combine(outputDirectory, $"{name}-vocals.wav");
            File.WriteAllBytes(instrumental, [1, 2]);
            File.WriteAllBytes(vocals, [3, 4]);
            progress?.Report(new OperationProgress(1, "fake stems complete"));
            return Task.FromResult(new StemSeparationResult(source.FilePath, instrumental, vocals));
        }
    }

    private sealed class FakeProcessing(List<string> log) : IAudioProcessingService
    {
        public AlignmentParameters? LastParameters { get; private set; }

        public Task<string> RenderAlignedVocalsAsync(
            string vocalFilePath,
            string outputFilePath,
            AlignmentParameters parameters,
            TimeSpan sourceDuration,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log.Add("render");
            LastParameters = parameters;
            Directory.CreateDirectory(Path.GetDirectoryName(outputFilePath)!);
            File.WriteAllBytes(outputFilePath, [9, 9, 9, 9]);
            progress?.Report(new OperationProgress(1, "fake render complete"));
            return Task.FromResult(outputFilePath);
        }
    }

    private sealed class CollectingProgress : IProgress<OperationProgress>
    {
        public List<OperationProgress> Values { get; } = [];

        public void Report(OperationProgress value) => Values.Add(value);
    }
}

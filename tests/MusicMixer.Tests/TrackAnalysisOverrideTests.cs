using MusicMixer.Application.Mashup;
using MusicMixer.Domain.Alignment;
using MusicMixer.Domain.Models;

namespace MusicMixer.Tests;

public sealed class TrackAnalysisOverrideTests
{
    private static readonly MusicalKey CMajor = new(PitchClass.C, KeyMode.Major);
    private static readonly MusicalKey DMajor = new(PitchClass.D, KeyMode.Major);

    [Fact]
    public void None_IsEmptyAndKeepsTrackUnchanged()
    {
        var track = CreateTrack("a.wav", 120, 0.5, CMajor);

        Assert.True(TrackAnalysisOverride.None.IsEmpty);
        Assert.Same(track, TrackAnalysisOverride.None.ApplyTo(track));
    }

    [Fact]
    public void ApplyTo_ReplacesOnlyProvidedValues()
    {
        var track = CreateTrack("a.wav", 120, 0.5, CMajor, tempoConfidence: 0.8, keyConfidence: 0.6);

        var result = new TrackAnalysisOverride(beatsPerMinute: 128).ApplyTo(track);

        Assert.Equal(128d, result.BeatsPerMinute!.Value, 6);
        Assert.Equal(TimeSpan.FromSeconds(0.5), result.DownbeatOffset);
        Assert.Equal(CMajor, result.Key);
        Assert.Equal(track.FilePath, result.FilePath);
        Assert.Equal(track.Duration, result.Duration);
        Assert.Equal(0.8, result.TempoConfidence!.Value, 6);
        Assert.Equal(0.6, result.KeyConfidence!.Value, 6);
    }

    [Fact]
    public void ApplyTo_CanReplaceDownbeatAndKey()
    {
        var track = CreateTrack("a.wav", 120, 0.5, CMajor);

        var result = new TrackAnalysisOverride(
            downbeatOffset: TimeSpan.FromSeconds(1.25),
            key: DMajor).ApplyTo(track);

        Assert.Equal(120d, result.BeatsPerMinute!.Value, 6);
        Assert.Equal(TimeSpan.FromSeconds(1.25), result.DownbeatOffset);
        Assert.Equal(DMajor, result.Key);
    }

    [Theory]
    [InlineData(39.9)]
    [InlineData(300.1)]
    [InlineData(double.NaN)]
    public void Constructor_RejectsBpmOutsideRange(double bpm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrackAnalysisOverride(beatsPerMinute: bpm));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60001)]
    public void Constructor_RejectsDownbeatOutsideRange(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TrackAnalysisOverride(downbeatOffset: TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void WithAnalysis_KeepsAnalyzerConfidence()
    {
        var analysis = new AudioAnalysisResult(
            TimeSpan.FromMinutes(3),
            124,
            TimeSpan.FromSeconds(0.25),
            DMajor,
            0.9,
            0.4);

        var track = AudioTrack
            .CreateUnanalyzed(Path.Combine(Path.GetTempPath(), "x.wav"))
            .WithAnalysis(analysis);

        Assert.Equal(0.9, track.TempoConfidence!.Value, 6);
        Assert.Equal(0.4, track.KeyConfidence!.Value, 6);
    }

    [Fact]
    public void Session_EffectiveTracksUseOverridesAndKeepAutomaticAnalysis()
    {
        var session = CreateSession().WithAnalysisOverrides(
            TrackAnalysisOverride.None,
            new TrackAnalysisOverride(beatsPerMinute: 120, key: CMajor));

        Assert.Equal(100d, session.VocalTrack.BeatsPerMinute!.Value, 6);
        Assert.Equal(DMajor, session.VocalTrack.Key);
        Assert.Equal(120d, session.EffectiveVocalTrack.BeatsPerMinute!.Value, 6);
        Assert.Equal(CMajor, session.EffectiveVocalTrack.Key);
        Assert.Same(session.InstrumentalTrack, session.EffectiveInstrumentalTrack);
    }

    [Fact]
    public void Session_WithAlignedVocal_PreservesOverrides()
    {
        var vocalOverride = new TrackAnalysisOverride(beatsPerMinute: 120);
        var session = CreateSession().WithAnalysisOverrides(TrackAnalysisOverride.None, vocalOverride);

        var updated = session.WithAlignedVocal(Path.Combine(Path.GetTempPath(), "aligned-2.wav"));

        Assert.Equal(vocalOverride, updated.VocalOverride);
        Assert.True(updated.InstrumentalOverride.IsEmpty);
    }

    [Fact]
    public void Alignment_UsesOverriddenTempoAndKey()
    {
        var session = CreateSession();
        var automatic = AlignmentCalculator.Calculate(
            session.EffectiveInstrumentalTrack,
            session.EffectiveVocalTrack);

        var corrected = session.WithAnalysisOverrides(
            TrackAnalysisOverride.None,
            new TrackAnalysisOverride(beatsPerMinute: 120, key: CMajor));
        var manual = AlignmentCalculator.Calculate(
            corrected.EffectiveInstrumentalTrack,
            corrected.EffectiveVocalTrack);

        Assert.Equal(1.2, automatic.AutomaticPlaybackSpeedRatio, 6);
        Assert.NotEqual(0d, automatic.AutomaticPitchShiftSemitones);
        Assert.Equal(1.0, manual.AutomaticPlaybackSpeedRatio, 6);
        Assert.Equal(0, manual.AutomaticPitchShiftSemitones, 6);
    }

    private static MashupSession CreateSession()
    {
        var root = Path.GetTempPath();
        var instrumental = CreateTrack("inst.wav", 120, 0.5, CMajor);
        var vocal = CreateTrack("vocal.wav", 100, 0.25, DMajor);
        return new MashupSession(
            instrumental,
            vocal,
            new StemSeparationResult(
                instrumental.FilePath,
                Path.Combine(root, "inst-instrumental.wav"),
                Path.Combine(root, "inst-vocals.wav")),
            new StemSeparationResult(
                vocal.FilePath,
                Path.Combine(root, "vocal-instrumental.wav"),
                Path.Combine(root, "vocal-vocals.wav")),
            Path.Combine(root, "aligned.wav"));
    }

    private static AudioTrack CreateTrack(
        string fileName,
        double bpm,
        double downbeatSeconds,
        MusicalKey key,
        double? tempoConfidence = null,
        double? keyConfidence = null)
    {
        var path = Path.Combine(Path.GetTempPath(), fileName);
        return new AudioTrack(
            path,
            TrackMetadata.FromFilePath(path),
            TimeSpan.FromMinutes(3),
            bpm,
            TimeSpan.FromSeconds(downbeatSeconds),
            key,
            tempoConfidence,
            keyConfidence);
    }
}

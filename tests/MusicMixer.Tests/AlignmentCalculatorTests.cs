using MusicMixer.Domain.Alignment;
using MusicMixer.Domain.Exceptions;
using MusicMixer.Domain.Models;

namespace MusicMixer.Tests;

public sealed class AlignmentCalculatorTests
{
    [Fact]
    public void Calculate_UsesTempoRatioKeyShiftAndDownbeatOffsets()
    {
        var instrumental = CreateTrack(
            @"C:\audio\instrumental.wav",
            beatsPerMinute: 128,
            downbeatSeconds: 0.5,
            new MusicalKey(PitchClass.C, KeyMode.Major));
        var vocal = CreateTrack(
            @"C:\audio\vocal.wav",
            beatsPerMinute: 120,
            downbeatSeconds: 0.75,
            new MusicalKey(PitchClass.B, KeyMode.Minor));

        var plan = AlignmentCalculator.Calculate(
            instrumental,
            vocal,
            finePitchSemitones: 0.5,
            offsetNudge: TimeSpan.FromMilliseconds(200));

        Assert.Equal(128d / 120d, plan.Parameters.PlaybackSpeedRatio, 6);
        Assert.Equal(128d / 120d, plan.AutomaticPlaybackSpeedRatio, 6);
        Assert.Equal(1.5, plan.Parameters.PitchShiftSemitones, 6);
        Assert.Equal(1.0, plan.AutomaticPitchShiftSemitones, 6);
        Assert.InRange(plan.AutomaticStartOffset.TotalSeconds, -0.301, -0.299);
        Assert.InRange(plan.Parameters.StartOffset.TotalMilliseconds, -101, -99);
    }

    [Fact]
    public void Calculate_ZeroFineTuningUsesAutomaticValues()
    {
        var instrumental = CreateTrack(
            @"C:\audio\instrumental.wav",
            beatsPerMinute: 100,
            downbeatSeconds: 1.0,
            new MusicalKey(PitchClass.A, KeyMode.Minor));
        var vocal = CreateTrack(
            @"C:\audio\vocal.wav",
            beatsPerMinute: 100,
            downbeatSeconds: 1.0,
            new MusicalKey(PitchClass.C, KeyMode.Major));

        var plan = AlignmentCalculator.Calculate(instrumental, vocal);

        Assert.Equal(1.0, plan.Parameters.PlaybackSpeedRatio, 6);
        Assert.Equal(0.0, plan.Parameters.PitchShiftSemitones, 6);
        Assert.Equal(TimeSpan.Zero, plan.Parameters.StartOffset);
    }

    [Fact]
    public void Calculate_WhenTrackIsNotAnalyzed_Throws()
    {
        var instrumental = AudioTrack.CreateUnanalyzed(@"C:\audio\instrumental.wav");
        var vocal = AudioTrack.CreateUnanalyzed(@"C:\audio\vocal.wav");

        Assert.Throws<AlignmentException>(() => AlignmentCalculator.Calculate(instrumental, vocal));
    }

    [Fact]
    public void Calculate_ManualSpeedOverridesAutomaticTempo()
    {
        var instrumental = CreateTrack(
            @"C:\audio\instrumental.wav",
            beatsPerMinute: 128,
            downbeatSeconds: 0.5,
            new MusicalKey(PitchClass.C, KeyMode.Major));
        var vocal = CreateTrack(
            @"C:\audio\vocal.wav",
            beatsPerMinute: 120,
            downbeatSeconds: 0.75,
            new MusicalKey(PitchClass.C, KeyMode.Major));

        var plan = AlignmentCalculator.Calculate(
            instrumental,
            vocal,
            playbackSpeedRatioOverride: 1.0);

        Assert.Equal(1.0, plan.Parameters.PlaybackSpeedRatio, 6);
        Assert.Equal(128d / 120d, plan.AutomaticPlaybackSpeedRatio, 6);
    }

    [Theory]
    [InlineData(0.49)]
    [InlineData(2.01)]
    [InlineData(double.NaN)]
    public void Calculate_RejectsInvalidManualSpeed(double speedRatio)
    {
        var instrumental = CreateTrack(
            @"C:\audio\instrumental.wav",
            beatsPerMinute: 100,
            downbeatSeconds: 0,
            new MusicalKey(PitchClass.C, KeyMode.Major));
        var vocal = CreateTrack(
            @"C:\audio\vocal.wav",
            beatsPerMinute: 100,
            downbeatSeconds: 0,
            new MusicalKey(PitchClass.C, KeyMode.Major));

        Assert.Throws<AlignmentException>(() => AlignmentCalculator.Calculate(
            instrumental,
            vocal,
            playbackSpeedRatioOverride: speedRatio));
    }

    [Fact]
    public void Calculate_NormalizesTempoOctave()
    {
        var instrumental = CreateTrack(
            @"C:\audio\instrumental.wav",
            beatsPerMinute: 60,
            downbeatSeconds: 0,
            new MusicalKey(PitchClass.C, KeyMode.Major));
        var vocal = CreateTrack(
            @"C:\audio\vocal.wav",
            beatsPerMinute: 200,
            downbeatSeconds: 0,
            new MusicalKey(PitchClass.C, KeyMode.Major));

        var plan = AlignmentCalculator.Calculate(instrumental, vocal);

        // 60 / 200 = 0.3; после удвоения — 0.6.
        Assert.Equal(0.6, plan.Parameters.PlaybackSpeedRatio, 6);
    }

    [Fact]
    public void Calculate_NormalizesFastTempoOctave()
    {
        var instrumental = CreateTrack(
            @"C:\audio\instrumental.wav",
            beatsPerMinute: 180,
            downbeatSeconds: 0,
            new MusicalKey(PitchClass.C, KeyMode.Major));
        var vocal = CreateTrack(
            @"C:\audio\vocal.wav",
            beatsPerMinute: 60,
            downbeatSeconds: 0,
            new MusicalKey(PitchClass.C, KeyMode.Major));

        var plan = AlignmentCalculator.Calculate(instrumental, vocal);

        // 180 / 60 = 3.0; после деления на два — 1.5.
        Assert.Equal(1.5, plan.Parameters.PlaybackSpeedRatio, 6);
    }

    private static AudioTrack CreateTrack(
        string path,
        double beatsPerMinute,
        double downbeatSeconds,
        MusicalKey key) =>
        new(
            path,
            new TrackMetadata(Path.GetFileNameWithoutExtension(path)),
            TimeSpan.FromMinutes(3),
            beatsPerMinute,
            TimeSpan.FromSeconds(downbeatSeconds),
            key);
}

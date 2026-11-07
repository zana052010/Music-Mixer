using MusicMixer.Domain.Models;

namespace MusicMixer.Tests;

public sealed class AlignmentParametersTests
{
    [Theory]
    [InlineData(12, 2.0)]
    [InlineData(-12, 0.5)]
    [InlineData(0, 1.0)]
    public void PitchScaleRatio_ConvertsSemitonesToFrequencyRatio(double semitones, double expected)
    {
        var parameters = new AlignmentParameters(1.0, semitones, TimeSpan.Zero);

        Assert.Equal(expected, parameters.PitchScaleRatio, 9);
    }

    [Theory]
    [InlineData(0.49)]
    [InlineData(2.01)]
    [InlineData(double.NaN)]
    public void Constructor_RejectsInvalidTempoRatio(double ratio)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AlignmentParameters(ratio, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(-24.1)]
    [InlineData(24.1)]
    public void Constructor_RejectsExtremePitchShift(double semitones)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AlignmentParameters(1, semitones, TimeSpan.Zero));
    }
}

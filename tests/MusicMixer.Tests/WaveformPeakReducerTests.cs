using MusicMixer.Application.Audio;

namespace MusicMixer.Tests;

public sealed class WaveformPeakReducerTests
{
    [Fact]
    public void Reduce_EmptyWindows_ReturnsEmptyArray()
    {
        var peaks = WaveformPeakReducer.Reduce([], 12);

        Assert.Empty(peaks);
    }

    [Fact]
    public void Reduce_UsesMaximumPerBucketAndNormalizesGlobalPeak()
    {
        var windows = new[] { 0.1f, 0.5f, 0.2f, 1.0f, 0.25f, 0.5f, 0.0f, 0.75f };

        var peaks = WaveformPeakReducer.Reduce(windows, 2);

        Assert.Equal(2, peaks.Length);
        // Первая половина: максимум 1.0, вторая: 0.75. Нормировка к 1 и
        // мягкая гамма сохраняют относительный порядок.
        Assert.Equal(1.0f, peaks[0], precision: 5);
        Assert.True(peaks[1] > 0);
        Assert.True(peaks[1] < peaks[0]);
    }

    [Fact]
    public void Reduce_MoreBucketsThanWindows_KeepsRequestedLength()
    {
        var peaks = WaveformPeakReducer.Reduce([0.2f, 0.4f, 0.6f], 8);

        Assert.Equal(8, peaks.Length);
        Assert.All(peaks, value => Assert.InRange(value, 0, 1));
        Assert.Contains(peaks, value => value > 0);
    }

    [Fact]
    public void Reduce_AllSilent_ReturnsZeros()
    {
        var peaks = WaveformPeakReducer.Reduce([0f, 0f, 0f], 4);

        Assert.All(peaks, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void Reduce_IgnoresNonFiniteValues()
    {
        var peaks = WaveformPeakReducer.Reduce(
            [float.NaN, 0.2f, float.PositiveInfinity, 0.4f],
            2);

        Assert.All(peaks, value => Assert.True(float.IsFinite(value)));
        Assert.Equal(1.0f, peaks[1], precision: 5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reduce_RejectsNonPositiveBucketCount(int bucketCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WaveformPeakReducer.Reduce([0.1f], bucketCount));
    }
}

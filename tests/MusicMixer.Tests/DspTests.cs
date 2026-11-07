using MusicMixer.Infrastructure.Analysis.Dsp;
using MusicMixer.Infrastructure.Configuration;

namespace MusicMixer.Tests;

public sealed class DspTests
{
    [Fact]
    public void Fft_ResolvesSineWavePeakAtExpectedBin()
    {
        const int sampleRate = 8_192;
        const int size = 1_024;
        const double frequency = 1_000;
        var real = new double[size];
        var imaginary = new double[size];

        for (var index = 0; index < size; index++)
        {
            real[index] = Math.Sin(2 * Math.PI * frequency * index / sampleRate);
        }

        FastFourierTransform.Transform(real, imaginary);

        var magnitudes = Enumerable.Range(1, (size / 2) - 1)
            .Select(bin => (Bin: bin, Magnitude: Math.Sqrt((real[bin] * real[bin]) + (imaginary[bin] * imaginary[bin]))))
            .ToArray();
        var peak = magnitudes.MaxBy(item => item.Magnitude);

        Assert.InRange(peak.Bin, 124, 126);
    }

    [Fact]
    public void TempoDetector_DetectsSyntheticClickTrack()
    {
        const int sampleRate = 22_050;
        const double expectedBpm = 120;
        const int seconds = 24;
        var samples = CreateClickTrack(sampleRate, expectedBpm, seconds);
        var options = new AudioAnalysisOptions();

        var estimate = TempoDetector.Analyze(samples, sampleRate, options, CancellationToken.None);

        Assert.InRange(estimate.BeatsPerMinute, expectedBpm - 1.0, expectedBpm + 1.0);
        Assert.InRange(estimate.DownbeatOffset.TotalSeconds, 0, (60.0 / expectedBpm) * 4);
        Assert.True(estimate.Confidence > 0);
    }

    private static float[] CreateClickTrack(int sampleRate, double bpm, int seconds)
    {
        var samples = new float[sampleRate * seconds];
        var random = new Random(17);
        var beatPeriod = 60.0 / bpm;

        for (var beat = 0; beat * beatPeriod < seconds; beat++)
        {
            var accent = beat % 4 == 0;
            var start = (int)Math.Round((0.1 + (beat * beatPeriod)) * sampleRate);
            var burstLength = (int)(0.09 * sampleRate);
            for (var offset = 0; offset < burstLength && start + offset < samples.Length; offset++)
            {
                var time = offset / (double)sampleRate;
                var envelope = Math.Exp(-time / 0.024);
                var kick = Math.Sin(2 * Math.PI * 58 * time) * envelope;
                var click = (random.NextDouble() * 2 - 1) * Math.Exp(-time / 0.003);
                var gain = accent ? 1.0 : 0.62;
                samples[start + offset] += (float)(gain * ((0.8 * kick) + (0.35 * click)));
            }
        }

        return samples;
    }
}

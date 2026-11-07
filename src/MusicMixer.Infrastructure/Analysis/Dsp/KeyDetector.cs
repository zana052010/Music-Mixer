// Инфраструктурный слой: DSP-определение тональности по хрома-профилям.
// Вызывается из AudioAnalyzerService после декодирования моно PCM.

using MusicMixer.Domain.Exceptions;
using MusicMixer.Domain.Models;

namespace MusicMixer.Infrastructure.Analysis.Dsp;

/// <summary>
/// Результат определения тональности: тоника с ладом и уверенность от 0 до 1.
/// </summary>
internal readonly record struct KeyEstimate(MusicalKey Key, double Confidence);

/// <summary>
/// Определяет тональность методом Крумхансл–Кесслера: усреднённая хрома коррелирует с мажорным и минорным профилями.
/// </summary>
internal static class KeyDetector
{
    private const int FrameSize = 4096;
    private const int HopSize = 4096;
    private const double MinimumFrequencyHz = 65.0;
    private const double MaximumFrequencyHz = 5000.0;

    // Профили Крумхансл–Кесслера: ожидаемая относительная сила каждой ступени лада,
    // усреднённая по западной тональной музыке. Индекс 0 — тоника.
    private static readonly double[] MajorProfile =
    [
        6.35, 2.23, 3.48, 2.33, 4.38, 4.09,
        2.52, 5.19, 2.39, 3.66, 2.29, 2.88,
    ];

    private static readonly double[] MinorProfile =
    [
        6.33, 2.68, 3.52, 5.38, 2.60, 3.53,
        2.54, 4.75, 3.98, 2.69, 3.34, 3.17,
    ];

    /// <summary>
    /// Строит усреднённую хрома-гистограмму по кадрам и выбирает тональность с лучшей корреляцией с профилем.
    /// </summary>
    public static KeyEstimate Detect(
        float[] samples,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        var frameCount = ((samples.Length - FrameSize) / HopSize) + 1;
        if (frameCount < 3)
        {
            throw new AudioAnalysisException("Not enough audio to analyze musical key.");
        }

        var window = FastFourierTransform.CreateHannWindow(FrameSize);
        var real = new double[FrameSize];
        var imaginary = new double[FrameSize];
        var accumulatedChroma = new double[12];
        var usedFrames = 0;

        // Ниже 65 Гц (примерно C2) и выше 5 кГц гармоники инструментов только
        // размывают хрому, поэтому анализируем ограниченную полосу спектра.
        var minimumBin = Math.Max(1, (int)Math.Ceiling(MinimumFrequencyHz * FrameSize / sampleRate));
        var maximumBin = Math.Min(
            FrameSize / 2 - 1,
            (int)Math.Floor(MaximumFrequencyHz * FrameSize / sampleRate));

        for (var frame = 0; frame < frameCount; frame++)
        {
            if ((frame & 31) == 0)
            {
                // Отмену проверяем раз в 32 кадра, чтобы не трогать токен на каждой итерации.
                cancellationToken.ThrowIfCancellationRequested();
            }

            Array.Clear(imaginary);
            var sampleOffset = frame * HopSize;
            for (var index = 0; index < FrameSize; index++)
            {
                real[index] = samples[sampleOffset + index] * window[index];
            }

            FastFourierTransform.Transform(real, imaginary);
            var frameChroma = new double[12];
            var frameEnergy = 0.0;

            for (var bin = minimumBin; bin <= maximumBin; bin++)
            {
                var magnitude = Math.Sqrt((real[bin] * real[bin]) + (imaginary[bin] * imaginary[bin]));
                if (magnitude <= 1e-12)
                {
                    continue;
                }

                var frequency = (double)bin * sampleRate / FrameSize;
                var midi = 69.0 + (12.0 * Math.Log2(frequency / 440.0));
                var pitchClass = midi - (12.0 * Math.Floor(midi / 12.0));
                var lower = (int)Math.Floor(pitchClass);
                var fraction = pitchClass - lower;
                var lowerClass = lower % 12;
                var upperClass = (lower + 1) % 12;

                frameChroma[lowerClass] += magnitude * (1.0 - fraction);
                frameChroma[upperClass] += magnitude * fraction;
                frameEnergy += magnitude;
            }

            if (frameEnergy <= 1e-6)
            {
                continue;
            }

            // Normalizing every frame prevents loud sections from dominating
            // the global pitch-class histogram.
            var norm = Math.Sqrt(frameChroma.Sum(value => value * value));
            if (norm <= 1e-12)
            {
                continue;
            }

            for (var note = 0; note < 12; note++)
            {
                accumulatedChroma[note] += frameChroma[note] / norm;
            }

            usedFrames++;
        }

        if (usedFrames < 3)
        {
            throw new AudioAnalysisException("No tonal content was found for key detection.");
        }

        var normalise = Math.Sqrt(accumulatedChroma.Sum(value => value * value));
        for (var note = 0; note < 12; note++)
        {
            accumulatedChroma[note] /= normalise;
        }

        var bestScore = double.NegativeInfinity;
        var secondScore = double.NegativeInfinity;
        var bestTonic = PitchClass.C;
        var bestMode = KeyMode.Major;

        for (var tonic = 0; tonic < 12; tonic++)
        {
            var majorScore = Correlation(accumulatedChroma, MajorProfile, tonic);
            var minorScore = Correlation(accumulatedChroma, MinorProfile, tonic);

            if (majorScore > bestScore)
            {
                secondScore = bestScore;
                bestScore = majorScore;
                bestTonic = (PitchClass)tonic;
                bestMode = KeyMode.Major;
            }
            else if (majorScore > secondScore)
            {
                secondScore = majorScore;
            }

            if (minorScore > bestScore)
            {
                secondScore = bestScore;
                bestScore = minorScore;
                bestTonic = (PitchClass)tonic;
                bestMode = KeyMode.Minor;
            }
            else if (minorScore > secondScore)
            {
                secondScore = minorScore;
            }
        }

        if (!double.IsFinite(bestScore))
        {
            throw new AudioAnalysisException("Key detection failed to score the analyzed frames.");
        }

        var confidence = double.IsFinite(secondScore)
            ? Math.Clamp((bestScore - secondScore) * 2.0, 0, 1)
            : 0.5;

        return new KeyEstimate(new MusicalKey(bestTonic, bestMode), confidence);
    }

    private static double Correlation(double[] chroma, double[] profile, int tonic)
    {
        var chromaMean = chroma.Average();
        var profileMean = profile.Average();
        double numerator = 0;
        double chromaSquares = 0;
        double profileSquares = 0;

        for (var index = 0; index < 12; index++)
        {
            var profileValue = profile[index] - profileMean;
            var chromaValue = chroma[(tonic + index) % 12] - chromaMean;
            numerator += chromaValue * profileValue;
            chromaSquares += chromaValue * chromaValue;
            profileSquares += profileValue * profileValue;
        }

        var denominator = Math.Sqrt(chromaSquares * profileSquares);
        return denominator <= 1e-12 ? 0 : numerator / denominator;
    }
}

// Детектор темпа и даунбита: спектральный поток, автокорреляция и выбор фазы.
// Слой Infrastructure, internal DSP. Вызывается из AudioAnalyzerService.
// Алгоритм работает по моно PCM и не зависит от внешних инструментов.
using MusicMixer.Domain.Exceptions;
using MusicMixer.Infrastructure.Configuration;

namespace MusicMixer.Infrastructure.Analysis.Dsp;

/// <summary>
/// Результат оценки темпа: BPM, смещение первого даунбита и уверенность 0..1.
/// </summary>
internal readonly record struct TempoEstimate(
    double BeatsPerMinute,
    TimeSpan DownbeatOffset,
    double Confidence);

/// <summary>
/// Эвристический детектор для музыки 4/4. Сначала строит огибающие вступлений,
/// затем ищет период импульсов автокорреляцией, уточняет его и выбирает фазу
/// сетки, на которую лучше всего ложатся удары.
/// </summary>
internal static class TempoDetector
{
    // Окно 1024 при hop 256 даёт достаточно частотного разрешения и
    // перекрытие, чтобы не потерять короткие удары между кадрами.
    private const int FrameSize = 1024;
    private const int HopSize = 256;
    // Приор тянет выбор к типичному танцевальному/поп-темпу и помогает
    // разрешить неоднозначность «половина или двойной темп».
    private const double PreferredBpm = 120.0;
    private const double TempoPriorOctaveWidth = 0.85;

    public static TempoEstimate Analyze(
        float[] samples,
        int sampleRate,
        AudioAnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var frameCount = ((samples.Length - FrameSize) / HopSize) + 1;
        if (frameCount < 128)
        {
            throw new AudioAnalysisException("Not enough audio to detect tempo.");
        }

        // Две огибающие: полная (для темпа) и низкочастотная (для даунбита,
        // где сильнее всего звучат бочка и бас).
        var onsetEnvelope = new float[frameCount];
        var lowOnsetEnvelope = new float[frameCount];
        ExtractOnsetEnvelopes(
            samples,
            sampleRate,
            frameCount,
            onsetEnvelope,
            lowOnsetEnvelope,
            cancellationToken);

        SmoothInPlace(onsetEnvelope);
        SmoothInPlace(lowOnsetEnvelope);

        var framesPerSecond = sampleRate / (double)HopSize;
        // Лаг автокорреляции — период доли в кадрах. Границы задаёт
        // пользовательский диапазон BPM (по умолчанию 60–200).
        var minLag = Math.Max(2, (int)Math.Floor(60.0 * framesPerSecond / options.MaximumBpm));
        var maxLag = Math.Min(frameCount - 3, (int)Math.Ceiling(60.0 * framesPerSecond / options.MinimumBpm));
        if (maxLag <= minLag + 2)
        {
            throw new AudioAnalysisException("The configured BPM range is too narrow for the decoded audio.");
        }

        var autocorrelation = ComputeAutocorrelation(
            onsetEnvelope,
            maxLag * 2,
            cancellationToken);

        var candidateCount = maxLag - minLag + 1;
        var scores = new double[candidateCount];
        var bestIndex = -1;
        var bestScore = double.NegativeInfinity;

        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var bpm = 60.0 * framesPerSecond / lag;

            // Логнормальный приор: чем дальше темп от 120 BPM по логарифму,
            // тем меньше итоговый балл. Логарифм нужен, чтобы 60 и 240 BPM
            // были одинаково далеки от 120.
            var prior = Math.Exp(-0.5 * Math.Pow(
                Math.Log2(bpm / PreferredBpm) / TempoPriorOctaveWidth,
                2));

            // Добавляем корреляцию на удвоенном лаге: это усиливает
            // половинный темп, если реальный пульс вдвое медленнее сетки.
            var harmonic = autocorrelation[lag];
            var doubleLag = lag * 2;
            if (doubleLag < autocorrelation.Length)
            {
                harmonic += 0.35 * autocorrelation[doubleLag];
            }

            var score = harmonic * prior;
            var index = lag - minLag;
            scores[index] = score;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        if (bestIndex < 0 || bestScore <= 0)
        {
            throw new AudioAnalysisException("Tempo detection did not find a reliable rhythmic pulse.");
        }

        var meanScore = scores.Average();
        var confidence = Math.Clamp((bestScore - meanScore) / bestScore, 0, 1);

        var bestLag = minLag + bestIndex;
        var refinedLag = (double)bestLag;
        // Параболическая интерполяция по трём соседним точкам: пик
        // автокорреляции редко попадает ровно в целый лаг.
        if (bestIndex > 0 && bestIndex < scores.Length - 1)
        {
            var previous = scores[bestIndex - 1];
            var current = scores[bestIndex];
            var next = scores[bestIndex + 1];
            var denominator = previous - (2 * current) + next;
            if (Math.Abs(denominator) > 1e-12)
            {
                var offset = 0.5 * (previous - next) / denominator;
                refinedLag += Math.Clamp(offset, -0.5, 0.5);
            }
        }

        var detectedBpm = 60.0 * framesPerSecond / refinedLag;
        if (!double.IsFinite(detectedBpm) || detectedBpm <= 0)
        {
            throw new AudioAnalysisException("Tempo detection produced an invalid BPM value.");
        }

        // Фаза доли — сдвиг сетки внутри периода; фаза даунбита — какая из
        // четырёх долей такта является первой.
        var phase = FindBeatPhase(onsetEnvelope, refinedLag);
        var downbeatPhase = FindDownbeatPhase(
            onsetEnvelope,
            lowOnsetEnvelope,
            phase,
            refinedLag);
        var downbeatFrames = phase + (downbeatPhase * refinedLag);
        var downbeatOffset = TimeSpan.FromSeconds(downbeatFrames / framesPerSecond);

        return new TempoEstimate(
            Math.Clamp(detectedBpm, options.MinimumBpm, options.MaximumBpm),
            downbeatOffset,
            confidence);
    }

    private static void ExtractOnsetEnvelopes(
        float[] samples,
        int sampleRate,
        int frameCount,
        float[] onsetEnvelope,
        float[] lowOnsetEnvelope,
        CancellationToken cancellationToken)
    {
        var window = FastFourierTransform.CreateHannWindow(FrameSize);
        var real = new double[FrameSize];
        var imaginary = new double[FrameSize];
        var previousLogMagnitude = new double[(FrameSize / 2) + 1];
        var lowBinLimit = Math.Max(2, (int)(250.0 * sampleRate / FrameSize));
        var nyquistBin = FrameSize / 2;

        for (var frame = 0; frame < frameCount; frame++)
        {
            if ((frame & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Array.Clear(imaginary);
            var sampleOffset = frame * HopSize;
            for (var index = 0; index < FrameSize; index++)
            {
                real[index] = samples[sampleOffset + index] * window[index];
            }

            FastFourierTransform.Transform(real, imaginary);
            double flux = 0;
            double lowFlux = 0;

            // Спектральный поток = сумма положительных приростов логарифма
            // амплитуды. Именно резкий рост энергии соответствует удару.
            for (var bin = 1; bin < nyquistBin; bin++)
            {
                var magnitude = Math.Sqrt((real[bin] * real[bin]) + (imaginary[bin] * imaginary[bin]));
                var logMagnitude = Math.Log(1.0 + magnitude);
                var difference = logMagnitude - previousLogMagnitude[bin];
                previousLogMagnitude[bin] = logMagnitude;

                if (difference > 0)
                {
                    flux += difference;
                    if (bin <= lowBinLimit)
                    {
                        lowFlux += difference;
                    }
                }
            }

            onsetEnvelope[frame] = (float)flux;
            lowOnsetEnvelope[frame] = (float)lowFlux;
        }
    }

    /// <summary>
    /// Сглаживает огибающую скользящим окном [0.25, 0.5, 0.25], чтобы
    /// одиночные выбросы не создавали ложных пиков.
    /// </summary>
    private static void SmoothInPlace(float[] values)
    {
        if (values.Length < 3)
        {
            return;
        }

        var previous = values[0];
        for (var index = 1; index < values.Length - 1; index++)
        {
            var current = values[index];
            values[index] = (float)((0.25 * previous) + (0.5 * current) + (0.25 * values[index + 1]));
            previous = current;
        }
    }

    /// <summary>
    /// Нормированная автокорреляция огибающей: высокий пик на лаге L означает,
    /// что через L кадров рисунок ударов повторяется.
    /// </summary>
    private static double[] ComputeAutocorrelation(
        float[] envelope,
        int maximumLag,
        CancellationToken cancellationToken)
    {
        var length = envelope.Length;
        maximumLag = Math.Min(maximumLag, length - 2);
        var mean = 0.0;
        for (var index = 0; index < length; index++)
        {
            mean += envelope[index];
        }

        mean /= length;

        var autocorrelation = new double[maximumLag + 1];
        for (var lag = 0; lag <= maximumLag; lag++)
        {
            if ((lag & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var sum = 0.0;
            var count = length - lag;
            for (var index = 0; index < count; index++)
            {
                sum += (envelope[index] - mean) * (envelope[index + lag] - mean);
            }

            autocorrelation[lag] = count > 0 ? sum / count : 0;
        }

        return autocorrelation;
    }

    /// <summary>
    /// Перебирает сдвиги сетки с шагом полкадра и выбирает тот, где сумма
    /// энергии вступлений максимальна.
    /// </summary>
    private static double FindBeatPhase(float[] onsetEnvelope, double beatPeriodFrames)
    {
        var bestPhase = 0.0;
        var bestScore = double.NegativeInfinity;

        const double phaseStep = 0.5;
        for (var phase = 0.0; phase < beatPeriodFrames; phase += phaseStep)
        {
            var score = ScoreBeatGrid(onsetEnvelope, phase, beatPeriodFrames);
            if (score > bestScore)
            {
                bestScore = score;
                bestPhase = phase;
            }
        }

        if (!double.IsFinite(bestScore) || bestScore <= 0)
        {
            throw new AudioAnalysisException("Could not establish a stable beat grid.");
        }

        return bestPhase;
    }

    /// <summary>
    /// Проверяет четыре возможных положения первой доли такта. Низкие частоты
    /// весят больше: бочка и бас обычно подчёркивают именно даунбит.
    /// </summary>
    private static int FindDownbeatPhase(
        float[] onsetEnvelope,
        float[] lowOnsetEnvelope,
        double phase,
        double beatPeriodFrames)
    {
        var bestSlot = 0;
        var bestScore = double.NegativeInfinity;

        for (var slot = 0; slot < 4; slot++)
        {
            var totalOnset = 0.0;
            var totalLowOnset = 0.0;
            var beats = 0;

            for (var beat = slot; ; beat += 4)
            {
                var position = phase + (beat * beatPeriodFrames);
                if (position >= onsetEnvelope.Length - 1)
                {
                    break;
                }

                totalOnset += SampleEnvelope(onsetEnvelope, position);
                totalLowOnset += SampleEnvelope(lowOnsetEnvelope, position);
                beats++;
            }

            if (beats == 0)
            {
                continue;
            }

            // Низкие частоты весят 0.7: бочка и бас обычно отмечают даунбит
            // сильнее, чем широкополосные вступления.
            var score = ((totalLowOnset / beats) * 0.7) + ((totalOnset / beats) * 0.3);
            if (score > bestScore)
            {
                bestScore = score;
                bestSlot = slot;
            }
        }

        return bestSlot;
    }

    private static double ScoreBeatGrid(float[] envelope, double phase, double period)
    {
        var score = 0.0;
        var beats = 0;
        for (var position = phase; position < envelope.Length - 1; position += period)
        {
            score += SampleEnvelope(envelope, position);
            beats++;
        }

        return beats == 0 ? double.NegativeInfinity : score / beats;
    }

    /// <summary>
    /// Линейно интерполирует значение огибающей в дробной позиции: сетка
    /// долей почти никогда не совпадает с целым номером кадра.
    /// </summary>
    private static double SampleEnvelope(float[] envelope, double position)
    {
        var lowerIndex = (int)Math.Floor(position);
        if (lowerIndex < 0 || lowerIndex >= envelope.Length)
        {
            return 0;
        }

        if (lowerIndex == envelope.Length - 1)
        {
            return envelope[lowerIndex];
        }

        var fraction = position - lowerIndex;
        return (envelope[lowerIndex] * (1 - fraction)) + (envelope[lowerIndex + 1] * fraction);
    }
}

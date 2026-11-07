// Свёртка оконных пиков в фиксированное число столбиков волновой формы.
// Слой Application: чистая математика без медиа-зависимостей.
namespace MusicMixer.Application.Audio;

public static class WaveformPeakReducer
{
    /// <summary>
    /// Разбивает окна пиков на <paramref name="bucketCount"/> групп, берёт
    /// максимум в каждой и нормирует результат к 1. Группы не теряют данные:
    /// последний столбик всегда захватывает остаток окон.
    /// </summary>
    public static float[] Reduce(IReadOnlyList<float> windowPeaks, int bucketCount)
    {
        ArgumentNullException.ThrowIfNull(windowPeaks);
        if (bucketCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketCount));
        }

        if (windowPeaks.Count == 0)
        {
            return [];
        }

        var peaks = new float[bucketCount];
        var globalMaximum = 0f;
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = (int)((long)bucket * windowPeaks.Count / bucketCount);
            var end = (int)((long)(bucket + 1) * windowPeaks.Count / bucketCount);
            if (end <= start)
            {
                end = Math.Min(start + 1, windowPeaks.Count);
            }

            var maximum = 0f;
            for (var index = start; index < end; index++)
            {
                var value = MathF.Abs(windowPeaks[index]);
                if (!float.IsFinite(value))
                {
                    continue;
                }

                if (value > maximum)
                {
                    maximum = value;
                }
            }

            peaks[bucket] = maximum;
            if (maximum > globalMaximum)
            {
                globalMaximum = maximum;
            }
        }

        if (globalMaximum <= 0)
        {
            return peaks;
        }

        // Даже тихая дорожка должна читаться на экране, поэтому после
        // нормировки применяется мягкая гамма, поднимающая малые пики.
        for (var index = 0; index < peaks.Length; index++)
        {
            peaks[index] = MathF.Pow(peaks[index] / globalMaximum, 0.7f);
        }

        return peaks;
    }
}

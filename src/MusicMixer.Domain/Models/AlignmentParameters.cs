// Параметры рендера вокала: коэффициент темпа, сдвиг высоты тона и стартовое смещение.
// Слой Domain: неизменяемый снимок значений с проверкой допустимых диапазонов.
// Создаётся AlignmentCalculator, потребляется IAudioProcessingService и плеером.
namespace MusicMixer.Domain.Models;

/// <summary>
/// Готовые к рендерингу значения для выравнивания вокального стема
/// относительно инструментального.
/// </summary>
public sealed record AlignmentParameters
{
    // Границы защищают рендер от экстремальных значений. AlignmentCalculator
    // предварительно приводит отношение BPM к ближайшей октаве, поэтому
    // обычная ошибка детекции половинного/двойного темпа сюда не доходит.
    public const double MinimumPlaybackSpeedRatio = 0.5;
    public const double MaximumPlaybackSpeedRatio = 2.0;
    public const double MaximumPitchShiftSemitones = 24.0;

    /// <summary>
    /// Проверяет и фиксирует параметры; значения вне диапазонов отклоняются,
    /// чтобы ошибка анализа не дошла до внешнего рендера.
    /// </summary>
    public AlignmentParameters(
        double playbackSpeedRatio,
        double pitchShiftSemitones,
        TimeSpan startOffset)
    {
        if (!double.IsFinite(playbackSpeedRatio) ||
            playbackSpeedRatio < MinimumPlaybackSpeedRatio ||
            playbackSpeedRatio > MaximumPlaybackSpeedRatio)
        {
            throw new ArgumentOutOfRangeException(
                nameof(playbackSpeedRatio),
                $"Playback speed ratio must be between {MinimumPlaybackSpeedRatio:0.0} and {MaximumPlaybackSpeedRatio:0.0}.");
        }

        if (!double.IsFinite(pitchShiftSemitones) ||
            Math.Abs(pitchShiftSemitones) > MaximumPitchShiftSemitones)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pitchShiftSemitones),
                $"Pitch shift must be between -{MaximumPitchShiftSemitones:0.#} and +{MaximumPitchShiftSemitones:0.#} semitones.");
        }

        if (startOffset < TimeSpan.FromMinutes(-30) || startOffset > TimeSpan.FromMinutes(30))
        {
            // Ограничение не даёт случайному значению увести вокал далеко
            // за пределы обоих треков и получить пустой микс.
            throw new ArgumentOutOfRangeException(
                nameof(startOffset),
                "Start offset must be within +/-30 minutes.");
        }

        PlaybackSpeedRatio = playbackSpeedRatio;
        PitchShiftSemitones = pitchShiftSemitones;
        StartOffset = startOffset;
    }

    /// <summary>
    /// Коэффициент темпа Rubber Band. Больше единицы — вокал ускоряется.
    /// </summary>
    public double PlaybackSpeedRatio { get; }

    /// <summary>
    /// Сдвиг в полутонах, применяемый к вокалу. Ноль сохраняет исходную тональность.
    /// </summary>
    public double PitchShiftSemitones { get; }

    /// <summary>
    /// Время начала вокала относительно начала инструментала.
    /// Положительное значение вставляет тишину перед вокалом.
    /// </summary>
    public TimeSpan StartOffset { get; }

    /// <summary>Представление в миллисекундах для ползунков и отчётов UI.</summary>
    public double StartOffsetMilliseconds => StartOffset.TotalMilliseconds;

    /// <summary>
    /// Коэффициент высоты тона для Rubber Band (отношение частот, не полутоны).
    /// </summary>
    // Полутон — это корень двенадцатой степени из двух, поэтому
    // 12 полутонов дают ровно удвоение частоты.
    public double PitchScaleRatio => Math.Pow(2.0, PitchShiftSemitones / 12.0);

    /// <summary>Короткая строка для журналов и отладочных панелей.</summary>
    public string ToDisplayString() =>
        $"tempo {PlaybackSpeedRatio:0.000}x, pitch {PitchShiftSemitones:+0.00;-0.00;0.00} st, offset {StartOffset.TotalSeconds:+0.000;-0.000;0.000}s";
}

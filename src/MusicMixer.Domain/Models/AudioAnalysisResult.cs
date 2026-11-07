// Результат анализа одного файла: длительность, темп, фаза даунбита,
// тональность и уверенность. Слой Domain, создаётся AudioAnalyzerService.
namespace MusicMixer.Domain.Models;

/// <summary>
/// Результат анализа темпа, даунбита и тональности одного аудиофайла.
/// Значения валидируются в конструкторе, чтобы битые данные не дошли до
/// расчёта выравнивания.
/// </summary>
public sealed record AudioAnalysisResult
{
    public AudioAnalysisResult(
        TimeSpan duration,
        double beatsPerMinute,
        TimeSpan downbeatOffset,
        MusicalKey key,
        double tempoConfidence,
        double keyConfidence)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (!double.IsFinite(beatsPerMinute) || beatsPerMinute <= 0 || beatsPerMinute > 400)
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerMinute), "BPM must be a finite value between 0 and 400.");
        }

        if (downbeatOffset < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(downbeatOffset), "Downbeat offset cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(key);

        Duration = duration;
        BeatsPerMinute = beatsPerMinute;
        DownbeatOffset = downbeatOffset;
        Key = key;
        // Уверенность приходит от эвристик и может быть любой; в UI она имеет
        // смысл только в диапазоне 0..1, поэтому значения ограничиваются.
        TempoConfidence = ClampConfidence(tempoConfidence);
        KeyConfidence = ClampConfidence(keyConfidence);
    }

    public TimeSpan Duration { get; }

    public double BeatsPerMinute { get; }

    /// <summary>
    /// Время от начала файла до первого предполагаемого даунбита 4/4.
    /// Значение намеренно локально в пределах первого такта: художественные
    /// сдвиги дальше по сетке делает ручная подстройка в UI.
    /// </summary>
    public TimeSpan DownbeatOffset { get; }

    public MusicalKey Key { get; }

    /// <summary>Значение 0..1 для индикатора уверенности в UI.</summary>
    public double TempoConfidence { get; }

    /// <summary>Значение 0..1 для индикатора уверенности в UI.</summary>
    public double KeyConfidence { get; }

    private static double ClampConfidence(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}

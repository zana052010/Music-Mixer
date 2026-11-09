// Ручные поправки анализа одной дорожки: BPM, первая доля и тональность.
// Слой Domain. Пустое поле означает «оставить значение автоматического анализа».
// Создаётся UI, применяется MashupSession перед расчётом выравнивания.
namespace MusicMixer.Domain.Models;

/// <summary>
/// Значения, которыми пользователь заменяет результат анализа, если детектор
/// ошибся. Поля независимы: null оставляет автоматическое значение, поэтому
/// можно поправить только BPM и не трогать тональность.
/// </summary>
public sealed record TrackAnalysisOverride
{
    // Диапазон шире реальной музыки, но отсекает опечатки вроде 1280 BPM.
    public const double MinimumBeatsPerMinute = 40;
    public const double MaximumBeatsPerMinute = 300;

    public TrackAnalysisOverride(
        double? beatsPerMinute = null,
        TimeSpan? downbeatOffset = null,
        MusicalKey? key = null)
    {
        if (beatsPerMinute is { } bpm &&
            (!double.IsFinite(bpm) || bpm < MinimumBeatsPerMinute || bpm > MaximumBeatsPerMinute))
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatsPerMinute),
                $"BPM must be between {MinimumBeatsPerMinute:0} and {MaximumBeatsPerMinute:0}.");
        }

        if (downbeatOffset is { } offset &&
            (offset < TimeSpan.Zero || offset > MaximumDownbeatOffset))
        {
            throw new ArgumentOutOfRangeException(
                nameof(downbeatOffset),
                $"Downbeat offset must be between 0 and {MaximumDownbeatOffset.TotalSeconds:0} seconds.");
        }

        BeatsPerMinute = beatsPerMinute;
        DownbeatOffset = downbeatOffset;
        Key = key;
    }

    /// <summary>
    /// Первая доля ищется в начале трека; минуты хватает даже для длинного интро.
    /// </summary>
    public static TimeSpan MaximumDownbeatOffset { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Отсутствие поправок: используется только автоматический анализ.</summary>
    public static TrackAnalysisOverride None { get; } = new();

    public double? BeatsPerMinute { get; }

    /// <summary>Время от начала файла до первого даунбита 4/4.</summary>
    public TimeSpan? DownbeatOffset { get; }

    public MusicalKey? Key { get; }

    public bool IsEmpty => BeatsPerMinute is null && DownbeatOffset is null && Key is null;

    /// <summary>
    /// Возвращает трек с подставленными ручными значениями. Путь, метаданные,
    /// длительность и уверенность автоматического анализа сохраняются.
    /// </summary>
    public AudioTrack ApplyTo(AudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (IsEmpty)
        {
            return track;
        }

        return new AudioTrack(
            track.FilePath,
            track.Metadata,
            track.Duration,
            BeatsPerMinute ?? track.BeatsPerMinute,
            DownbeatOffset ?? track.DownbeatOffset,
            Key ?? track.Key,
            track.TempoConfidence,
            track.KeyConfidence);
    }
}

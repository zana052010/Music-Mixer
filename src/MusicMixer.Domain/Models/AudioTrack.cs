// Импортированный аудиофайл и результаты его анализа. Слой Domain.
// Неизменяемая запись: обновление анализа создаёт новый экземпляр через
// WithAnalysis, поэтому сессия может безопасно хранить ссылку на трек.
namespace MusicMixer.Domain.Models;

/// <summary>
/// Аудиофайл плюс выполненный над ним анализ. Запись неизменяема, поэтому
/// обновления порождают новый экземпляр, а не меняют существующий.
/// </summary>
public sealed record AudioTrack
{
    public AudioTrack(
        string filePath,
        TrackMetadata metadata,
        TimeSpan duration,
        double? beatsPerMinute = null,
        TimeSpan? downbeatOffset = null,
        MusicalKey? key = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(metadata);

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (beatsPerMinute is <= 0 or > 400)
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerMinute));
        }

        if (downbeatOffset < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(downbeatOffset));
        }

        FilePath = Path.GetFullPath(filePath);
        Metadata = metadata;
        Duration = duration;
        BeatsPerMinute = beatsPerMinute;
        DownbeatOffset = downbeatOffset;
        Key = key;
    }

    public string FilePath { get; }

    public TrackMetadata Metadata { get; }

    public TimeSpan Duration { get; }

    /// <summary>BPM после анализа; null, пока трек не проанализирован.</summary>
    public double? BeatsPerMinute { get; }

    /// <summary>Смещение первого даунбита от начала файла.</summary>
    public TimeSpan? DownbeatOffset { get; }

    public MusicalKey? Key { get; }

    /// <summary>
    /// True, когда есть все три значения, необходимые AlignmentCalculator.
    /// Одного BPM недостаточно: без тональности и даунбита план не построить.
    /// </summary>
    public bool HasAnalysis =>
        BeatsPerMinute is > 0 &&
        DownbeatOffset is not null &&
        Key is not null;

    /// <summary>
    /// Возвращает новый трек с результатами анализа. Если анализатор не смог
    /// определить длительность, сохраняется длительность, известная заранее.
    /// </summary>
    public AudioTrack WithAnalysis(AudioAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        return new AudioTrack(
            FilePath,
            Metadata,
            analysis.Duration > TimeSpan.Zero ? analysis.Duration : Duration,
            analysis.BeatsPerMinute,
            analysis.DownbeatOffset,
            analysis.Key);
    }

    /// <summary>
    /// Создаёт трек до анализа: метаданные берутся из имени файла,
    /// длительность станет известна после AnalyzeAsync.
    /// </summary>
    public static AudioTrack CreateUnanalyzed(string filePath) =>
        new(filePath, TrackMetadata.FromFilePath(filePath), TimeSpan.Zero);
}

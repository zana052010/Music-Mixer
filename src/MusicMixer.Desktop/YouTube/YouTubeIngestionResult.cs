// Результат импорта с YouTube: метаданные, локальный WAV и целевая дорожка.
// Слой Desktop, возвращается из диалога в MainViewModel.
using MusicMixer.Application.YouTube;

namespace MusicMixer.Desktop.YouTube;

/// <summary>
/// Куда направить скачанный трек: A — инструментал, B — вокал.
/// </summary>
public enum TrackTarget
{
    Instrumental,
    Vocals,
}

public sealed record YouTubeIngestionResult(
    YouTubeTrackItem Track,
    string AudioFilePath,
    TrackTarget Target);

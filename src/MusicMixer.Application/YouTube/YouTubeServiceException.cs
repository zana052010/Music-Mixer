// Ошибки интеграции с YouTube и их категории. Категория нужна, чтобы UI
// показал локализованный совет, а не технический текст YoutubeExplode.
using MusicMixer.Domain.Exceptions;

namespace MusicMixer.Application.YouTube;

/// <summary>
/// Причина сбоя YouTube. ViewModel преобразует её в ключ локализации.
/// </summary>
public enum YouTubeFailureKind
{
    SearchFailed,
    VideoUnavailable,
    VideoUnplayable,
    SignInRequired,
    AudioStreamUnavailable,
    DownloadFailed,
    ConversionFailed,
}

public sealed class YouTubeServiceException : MusicMixerException
{
    public YouTubeServiceException(
        YouTubeFailureKind kind,
        string message,
        Exception? innerException = null,
        string? diagnostics = null)
        : base(message, innerException)
    {
        Kind = kind;
        // Хвост вывода FFmpeg ограничен и показывается только как диагностика.
        Diagnostics = string.IsNullOrWhiteSpace(diagnostics) ? null : diagnostics.Trim();
    }

    public YouTubeFailureKind Kind { get; }

    public string? Diagnostics { get; }
}

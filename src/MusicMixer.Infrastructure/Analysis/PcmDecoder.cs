// Общий контракт декодирования аудио в моно PCM для анализа.
// Слой Infrastructure, internal: единственная реализация — FfmpegPcmDecoder.
namespace MusicMixer.Infrastructure.Analysis;

internal interface IPcmDecoder
{
    Task<DecodedPcm> DecodeMonoAsync(
        string filePath,
        int sampleRate,
        TimeSpan maxDuration,
        CancellationToken cancellationToken);
}

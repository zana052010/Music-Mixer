// Порт воспроизведения. Умеет две независимые вещи:
// 1) прослушивание одного исходного файла в окнах добавления/обрезки;
// 2) синхронный предпросмотр мэшапа из инструментала и готового вокала.
// Реализация — ManagedBassAudioPlayerService; вызывается из MainViewModel и диалогов.
using MusicMixer.Domain.Models;

namespace MusicMixer.Application.Abstractions;

/// <summary>
/// Микшер предпросмотра с двумя синхронными потоками. Растяжение времени и
/// сдвиг высоты тона сюда не входят: они уже «запечены» в файл вокала до
/// передачи плееру.
/// </summary>
public interface IAudioPlayerService : IDisposable
{
    bool IsLoaded { get; }

    bool IsPlaying { get; }

    TimeSpan Position { get; }

    TimeSpan Duration { get; }

    bool IsSourcePreviewPlaying { get; }

    TimeSpan SourcePreviewPosition { get; }

    TimeSpan SourcePreviewDuration { get; }

    /// <summary>
    /// Загружает один исходный файл для редактора обрезки. Не связано с
    /// двухпоточным предпросмотром мэшапа.
    /// </summary>
    Task<TimeSpan> LoadSourcePreviewAsync(
        string filePath,
        CancellationToken cancellationToken = default);

    void PlaySourcePreview();

    void PauseSourcePreview();

    void StopSourcePreview();

    void SeekSourcePreview(TimeSpan position);

    void UnloadSourcePreview();

    Task LoadAsync(
        string instrumentalFilePath,
        string vocalFilePath,
        AlignmentParameters alignment,
        CancellationToken cancellationToken = default);

    void Play();

    void Pause();

    void Stop();

    void Seek(TimeSpan position);

    void SetVolumes(double instrumentalVolume, double vocalVolume);

    /// <summary>
    /// Меняет стартовое смещение вокала в миллисекундах, не перезапуская микс.
    /// Положительное значение задерживает вокал.
    /// </summary>
    void SetStartOffset(TimeSpan startOffset);

    void Unload();
}

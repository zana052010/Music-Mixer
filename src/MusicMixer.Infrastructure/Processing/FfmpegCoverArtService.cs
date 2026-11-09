// Infrastructure/Processing: извлечение встроенной обложки (attached picture) из аудиофайла.
// Вызывается из MainViewModel через ICoverArtService после выбора трека;
// запускает ffmpeg через IProcessRunner. Исходный файл не изменяется.
using System.Globalization;
using MusicMixer.Application.Abstractions;
using MusicMixer.Infrastructure.Configuration;
using MusicMixer.Infrastructure.Processes;

namespace MusicMixer.Infrastructure.Processing;

public sealed class FfmpegCoverArtService : ICoverArtService
{
    // Карточка показывает обложку 72×72; 256 px хватает с запасом для HiDPI.
    private const int CoverSize = 256;

    private readonly IProcessRunner _processRunner;
    private readonly FfmpegOptions _ffmpegOptions;

    public FfmpegCoverArtService(IProcessRunner processRunner, FfmpegOptions ffmpegOptions)
    {
        _processRunner = processRunner;
        _ffmpegOptions = ffmpegOptions;
    }

    public async Task<string?> ExtractEmbeddedCoverAsync(
        string audioFilePath,
        string outputFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);

        if (!File.Exists(audioFilePath))
        {
            return null;
        }

        var outputPath = Path.GetFullPath(outputFilePath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        // Встроенная обложка в ffmpeg видна как видеопоток (attached picture):
        // берём первый кадр первого видеопотока и обрезаем по центру в квадрат.
        var size = CoverSize.ToString(CultureInfo.InvariantCulture);
        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-loglevel", "error",
            "-i", Path.GetFullPath(audioFilePath),
            "-map", "0:v:0",
            "-frames:v", "1",
            "-an",
            "-sn",
            "-dn",
            "-vf", "scale=" + size + ":" + size + ":force_original_aspect_ratio=increase,crop=" + size + ":" + size,
            outputPath,
        };

        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessLaunchRequest(_ffmpegOptions.ExecutablePath, arguments),
                null,
                cancellationToken).ConfigureAwait(false);

            var output = new FileInfo(outputPath);
            if (result.ExitCode == 0 && output.Exists && output.Length > 0)
            {
                return outputPath;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Нет ffmpeg или он упал: карточка просто останется без обложки.
        }

        TryDelete(outputPath);
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

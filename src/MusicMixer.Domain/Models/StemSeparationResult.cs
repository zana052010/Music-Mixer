// Результат разделения на два стема. Слой Domain.
// Создаётся DemucsCliSeparatorService, потребляется MashupWorkflow и плеером.
namespace MusicMixer.Domain.Models;

/// <summary>
/// Пути, созданные разделением на два стема: исходник, инструментал и вокал.
/// Все пути абсолютные, чтобы рабочий каталог не влиял на дальнейшие вызовы.
/// </summary>
public sealed record StemSeparationResult
{
    public StemSeparationResult(string sourceFilePath, string instrumentalPath, string vocalsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vocalsPath);

        SourceFilePath = Path.GetFullPath(sourceFilePath);
        InstrumentalPath = Path.GetFullPath(instrumentalPath);
        VocalsPath = Path.GetFullPath(vocalsPath);
    }

    public string SourceFilePath { get; }

    /// <summary>Стем без вокала: используется как ведущая дорожка мэшапа.</summary>
    public string InstrumentalPath { get; }

    /// <summary>Вокальный стем: растягивается и транспонируется перед превью.</summary>
    public string VocalsPath { get; }
}

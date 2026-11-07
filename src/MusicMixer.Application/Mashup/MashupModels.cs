// Модели результата рабочего процесса: что возвращается в UI после подготовки
// и после повторного рендера вокала. Слой Application, только данные.
using MusicMixer.Domain.Models;

namespace MusicMixer.Application.Mashup;

/// <summary>
/// Итог полной подготовки: неизменяемая сессия со всеми файлами и план
/// выравнивания, который был использован для первого рендера.
/// </summary>
public sealed record MashupPreparationResult(
    MashupSession Session,
    AlignmentPlan Alignment);

/// <summary>
/// Итог повторного рендера вокала: путь к новому WAV и обновлённый план.
/// </summary>
public sealed record AlignedVocalResult(
    string AlignedVocalPath,
    AlignmentPlan Alignment);

/// <summary>
/// Все файлы и проанализированные дорожки одной сессии. View-model хранит
/// сессию неизменяемой и при точной подстройке заменяет только путь к вокалу.
/// </summary>
public sealed record MashupSession
{
    public MashupSession(
        AudioTrack instrumentalTrack,
        AudioTrack vocalTrack,
        StemSeparationResult instrumentalStems,
        StemSeparationResult vocalStems,
        string alignedVocalPath)
    {
        ArgumentNullException.ThrowIfNull(instrumentalTrack);
        ArgumentNullException.ThrowIfNull(vocalTrack);
        ArgumentNullException.ThrowIfNull(instrumentalStems);
        ArgumentNullException.ThrowIfNull(vocalStems);
        ArgumentException.ThrowIfNullOrWhiteSpace(alignedVocalPath);

        InstrumentalTrack = instrumentalTrack;
        VocalTrack = vocalTrack;
        InstrumentalStems = instrumentalStems;
        VocalStems = vocalStems;
        AlignedVocalPath = Path.GetFullPath(alignedVocalPath);
    }

    public AudioTrack InstrumentalTrack { get; }

    public AudioTrack VocalTrack { get; }

    public StemSeparationResult InstrumentalStems { get; }

    public StemSeparationResult VocalStems { get; }

    // Инструментал берётся из стемов дорожки A, а исходный вокал — из стемов
    // дорожки B. AlignedVocalPath — уже растянутый и транспонированный файл.
    public string InstrumentalPath => InstrumentalStems.InstrumentalPath;

    public string SourceVocalPath => VocalStems.VocalsPath;

    public string AlignedVocalPath { get; }

    /// <summary>
    /// Возвращает копию сессии с новым отрендеренным вокалом; остальные файлы
    /// и метаданные переиспользуются.
    /// </summary>
    public MashupSession WithAlignedVocal(string alignedVocalPath) =>
        new(InstrumentalTrack, VocalTrack, InstrumentalStems, VocalStems, alignedVocalPath);
}

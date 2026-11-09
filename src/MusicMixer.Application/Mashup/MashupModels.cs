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
        string alignedVocalPath,
        TrackAnalysisOverride? instrumentalOverride = null,
        TrackAnalysisOverride? vocalOverride = null)
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
        InstrumentalOverride = instrumentalOverride ?? TrackAnalysisOverride.None;
        VocalOverride = vocalOverride ?? TrackAnalysisOverride.None;
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

    /// <summary>Ручные поправки анализа дорожки A; None — только автоматика.</summary>
    public TrackAnalysisOverride InstrumentalOverride { get; }

    /// <summary>Ручные поправки анализа дорожки B; None — только автоматика.</summary>
    public TrackAnalysisOverride VocalOverride { get; }

    /// <summary>
    /// Дорожка A с учётом ручных поправок. Именно её видит расчёт выравнивания,
    /// а исходный InstrumentalTrack остаётся нетронутым для кнопки «Авто».
    /// </summary>
    public AudioTrack EffectiveInstrumentalTrack => InstrumentalOverride.ApplyTo(InstrumentalTrack);

    /// <summary>Дорожка B с учётом ручных поправок.</summary>
    public AudioTrack EffectiveVocalTrack => VocalOverride.ApplyTo(VocalTrack);

    /// <summary>
    /// Возвращает копию сессии с новым отрендеренным вокалом; остальные файлы
    /// и метаданные переиспользуются.
    /// </summary>
    public MashupSession WithAlignedVocal(string alignedVocalPath) =>
        new(
            InstrumentalTrack,
            VocalTrack,
            InstrumentalStems,
            VocalStems,
            alignedVocalPath,
            InstrumentalOverride,
            VocalOverride);

    /// <summary>
    /// Возвращает копию сессии с новыми ручными поправками. Файлы не меняются:
    /// новый вокал появится только после RenderAlignedVocalAsync.
    /// </summary>
    public MashupSession WithAnalysisOverrides(
        TrackAnalysisOverride instrumentalOverride,
        TrackAnalysisOverride vocalOverride)
    {
        ArgumentNullException.ThrowIfNull(instrumentalOverride);
        ArgumentNullException.ThrowIfNull(vocalOverride);

        return new(
            InstrumentalTrack,
            VocalTrack,
            InstrumentalStems,
            VocalStems,
            AlignedVocalPath,
            instrumentalOverride,
            vocalOverride);
    }
}

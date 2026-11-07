// Ядро расчёта выравнивания: темп, тональность и смещение даунбита вокала.
// Слой Domain: чистая математика без файлов, UI и медиазависимостей.
// Вызывается из MashupWorkflow; сам использует KeyTransposition и модели Domain.
using MusicMixer.Domain.Exceptions;
using MusicMixer.Domain.Models;

namespace MusicMixer.Domain.Alignment;

/// <summary>
/// Чистая математика выравнивания. Класс не зависит от файловой системы,
/// UI и медиа, поэтому его проще всего покрывать юнит-тестами.
/// </summary>
public static class AlignmentCalculator
{
    /// <summary>
    /// Строит план выравнивания: коэффициент темпа, сдвиг по тональности и
    /// стартовое смещение вокала относительно инструментала.
    /// </summary>
    public static AlignmentPlan Calculate(
        AudioTrack instrumental,
        AudioTrack vocal,
        double finePitchSemitones = 0,
        TimeSpan? offsetNudge = null,
        double? playbackSpeedRatioOverride = null)
    {
        ArgumentNullException.ThrowIfNull(instrumental);
        ArgumentNullException.ThrowIfNull(vocal);

        // Без BPM, даунбита и тональности обоих треков формула не определена:
        // лучше явная доменная ошибка, чем рендер со случайными значениями.
        EnsureAnalyzed(instrumental, "instrumental");
        EnsureAnalyzed(vocal, "vocal");

        if (!double.IsFinite(finePitchSemitones))
        {
            throw new AlignmentException("Fine-pitch adjustment must be a finite number.");
        }

        var instrumentalBpm = instrumental.BeatsPerMinute!.Value;
        var vocalBpm = vocal.BeatsPerMinute!.Value;

        // Вокал приводим к темпу инструментала: отношение больше единицы
        // означает ускорение вокала, меньше единицы — замедление.
        var automaticPitchShift = KeyTransposition.CalculateSemitones(vocal.Key!, instrumental.Key!);

        // Детектор темпа иногда ошибается ровно на октаву (половинный или
        // двойной темп). Приводим отношение к ближайшей октаве в допустимом
        // диапазоне, иначе MashupWorkflow отклонял бы вполне пригодные треки.
        var automaticSpeedRatio = NormalizeTempoOctave(instrumentalBpm / vocalBpm);

        // Ручная скорость вокала из UI заменяет автоматическую, но не меняет
        // сохранённое автоматическое значение: кнопка «Авто» вернётся к нему.
        var speedRatio = playbackSpeedRatioOverride ?? automaticSpeedRatio;
        if (!double.IsFinite(speedRatio) ||
            speedRatio < AlignmentParameters.MinimumPlaybackSpeedRatio ||
            speedRatio > AlignmentParameters.MaximumPlaybackSpeedRatio)
        {
            throw new AlignmentException(
                $"Vocal speed ratio must be between " +
                $"{AlignmentParameters.MinimumPlaybackSpeedRatio:0.0} and " +
                $"{AlignmentParameters.MaximumPlaybackSpeedRatio:0.0}.");
        }

        // Даунбит вокала измерен на его исходной сетке, а после приведения
        // темпа к инструменталу координаты масштабируются на tempoRatio.
        // Умножение переводит фазу вокала в таймлайн инструментала.
        var automaticOffset = instrumental.DownbeatOffset!.Value -
            TimeSpan.FromTicks((long)Math.Round(
                vocal.DownbeatOffset!.Value.Ticks * speedRatio,
                MidpointRounding.AwayFromZero));

        // Ручная подстройка всегда добавляется к автоматике, поэтому UI может
        // пересчитывать план от нуля, не накапливая предыдущие сдвиги.
        var nudge = offsetNudge ?? TimeSpan.Zero;
        var finalOffset = automaticOffset + nudge;
        var totalPitchShift = automaticPitchShift + finePitchSemitones;

        try
        {
            // Валидация диапазонов в AlignmentParameters — последний барьер
            // перед внешним рендером, поэтому её ошибку переводим в доменную.
            var parameters = new AlignmentParameters(speedRatio, totalPitchShift, finalOffset);
            return new AlignmentPlan(
                parameters,
                automaticOffset,
                automaticPitchShift,
                automaticSpeedRatio);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new AlignmentException(
                $"The analyzed tracks cannot be aligned: {exception.Message}",
                exception);
        }
    }

    private static void EnsureAnalyzed(AudioTrack track, string role)
    {
        if (!track.HasAnalysis)
        {
            throw new AlignmentException(
                $"The {role} track must be analyzed before alignment can be calculated.");
        }
    }

    /// <summary>
    /// Приводит отношение BPM к половине/двойному темпу, пока оно не попадёт
    /// в диапазон, допустимый для Rubber Band. Так исправляется типичная
    /// ошибка анализа, когда один трек определён как 60 BPM, а другой — как 180.
    /// </summary>
    private static double NormalizeTempoOctave(double speedRatio)
    {
        while (speedRatio > AlignmentParameters.MaximumPlaybackSpeedRatio)
        {
            speedRatio /= 2.0;
        }

        while (speedRatio < AlignmentParameters.MinimumPlaybackSpeedRatio)
        {
            speedRatio *= 2.0;
        }

        return speedRatio;
    }
}

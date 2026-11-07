// План выравнивания: автоматический расчёт плюс параметры, реально ушедшие
// в рендер. Слой Domain, создаётся AlignmentCalculator.
namespace MusicMixer.Domain.Models;

/// <summary>
/// Хранит и автоматический результат анализа, и параметры, которые были
/// отрендерены. Благодаря этому точная подстройка и синхро-сдвиг считаются
/// заново от исходных значений, а не накапливаются поверх прошлых правок.
/// </summary>
public sealed record AlignmentPlan
{
    public AlignmentPlan(
        AlignmentParameters parameters,
        TimeSpan automaticStartOffset,
        double automaticPitchShiftSemitones,
        double automaticPlaybackSpeedRatio)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        Parameters = parameters;
        AutomaticStartOffset = automaticStartOffset;
        AutomaticPitchShiftSemitones = automaticPitchShiftSemitones;
        AutomaticPlaybackSpeedRatio = automaticPlaybackSpeedRatio;
    }

    public AlignmentParameters Parameters { get; }

    /// <summary>
    /// Темп, который дал только анализ BPM, без ручной скорости из UI.
    /// Нужен кнопке «Авто», чтобы вернуть исходное значение.
    /// </summary>
    public double AutomaticPlaybackSpeedRatio { get; }

    /// <summary>
    /// Смещение, которое дала только формула по даунбитам, без ручной подстройки.
    /// </summary>
    public TimeSpan AutomaticStartOffset { get; }

    /// <summary>
    /// Сдвиг тональности, который дала только музыкальная транспозиция,
    /// без точной высоты тона из UI.
    /// </summary>
    public double AutomaticPitchShiftSemitones { get; }
}

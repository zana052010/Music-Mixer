// Выбор минимальной транспозиции вокала в тональность инструментала.
// Слой Domain: чистая музыкальная арифметика по классам высоты тона.
// Вызывается из AlignmentCalculator; использует MusicalKey и KeyMode.
using MusicMixer.Domain.Models;

namespace MusicMixer.Domain.Alignment;

/// <summary>
/// Вычисляет наименьшую транспозицию, при которой вокал совместим с
/// тональностью инструментала. Параллельные мажор/минор дают ноль,
/// потому что используют один и тот же звукоряд.
/// </summary>
public static class KeyTransposition
{
    /// <summary>
    /// Возвращает сдвиг в полутонах в диапазоне -6..+5 от тональности вокала
    /// к тональности инструментала; параллельные мажор и минор дают ноль.
    /// </summary>
    public static int CalculateSemitones(MusicalKey source, MusicalKey target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var sourceTonic = source.TonicSemitone;
        var targetTonic = target.TonicSemitone;

        if (sourceTonic == targetTonic)
        {
            return 0;
        }

        // Параллельные тональности используют один и тот же звукоряд, поэтому
        // вокал можно оставить как есть, даже если тоника формально другая.
        var isRelativePair =
            (source.Mode == KeyMode.Minor &&
             target.Mode == KeyMode.Major &&
             Mod12(sourceTonic + 3) == targetTonic) ||
            (source.Mode == KeyMode.Major &&
             target.Mode == KeyMode.Minor &&
             Mod12(targetTonic + 3) == sourceTonic);

        if (isRelativePair)
        {
            return 0;
        }

        // Produces -6..+5. -6 and +6 are equivalent; favour the downward move.
        return Mod12(targetTonic - sourceTonic + 6) - 6;
    }

    private static int Mod12(int value) => ((value % 12) + 12) % 12;
}

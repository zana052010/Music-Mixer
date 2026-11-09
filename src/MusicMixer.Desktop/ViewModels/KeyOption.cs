// Пункт выпадающего списка тональностей в редакторе анализа. Слой Desktop.
// Список упорядочен по колесу Кэмелота, чтобы совместимые тональности стояли рядом.
using MusicMixer.Domain.Models;

namespace MusicMixer.Desktop.ViewModels;

public sealed record KeyOption(MusicalKey Key)
{
    /// <summary>Все 24 тональности в порядке 1A, 1B, 2A … 12B.</summary>
    public static IReadOnlyList<KeyOption> All { get; } = BuildAll();

    /// <summary>Подпись вида «C# min · 12A».</summary>
    public string Label => Key.ShortDisplayName;

    // ComboBox без шаблона показывает ToString(), поэтому возвращаем подпись.
    public override string ToString() => Label;

    private static KeyOption[] BuildAll() =>
        Enum.GetValues<KeyMode>()
            .SelectMany(mode => Enum.GetValues<PitchClass>()
                .Select(tonic => new MusicalKey(tonic, mode)))
            .OrderBy(key => key.Camelot.Length)
            .ThenBy(key => key.Camelot, StringComparer.Ordinal)
            .Select(key => new KeyOption(key))
            .ToArray();
}

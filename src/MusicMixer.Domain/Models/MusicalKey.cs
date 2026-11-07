// Музыкальная тональность: тоника, лад и нотация Кэмелота. Слой Domain.
// Используется анализом, KeyTransposition и отображением в UI.
namespace MusicMixer.Domain.Models;

/// <summary>
/// 12 классов высоты тона. Значения совпадают с полутонами от C,
/// поэтому арифметика транспозиции сводится к сложению по модулю 12.
/// </summary>
public enum PitchClass
{
    C = 0,
    CSharp = 1,
    D = 2,
    DSharp = 3,
    E = 4,
    F = 5,
    FSharp = 6,
    G = 7,
    GSharp = 8,
    A = 9,
    ASharp = 10,
    B = 11,
}

public enum KeyMode
{
    Major,
    Minor,
}

/// <summary>
/// Тональность: тоника и лад плюс производные имена и код Кэмелота.
/// Кэмелот-код нужен диджеям для быстрой проверки совместимости тональностей.
/// </summary>
public sealed record MusicalKey
{
    // Таблицы сопоставляют полутон тоники (C=0..B=11) с номером на колесе
    // Кэмелота. B — мажор, A — минор; совместимые тональности стоят рядом.
    private static readonly string[] MajorCamelot =
    [
        "8B", "3B", "10B", "5B", "12B", "7B",
        "2B", "9B", "4B", "11B", "6B", "1B",
    ];

    private static readonly string[] MinorCamelot =
    [
        "5A", "12A", "7A", "2A", "9A", "4A",
        "11A", "6A", "1A", "8A", "3A", "10A",
    ];

    private static readonly string[] SharpNames =
    [
        "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B",
    ];

    public MusicalKey(PitchClass tonic, KeyMode mode)
    {
        if (!Enum.IsDefined(tonic))
        {
            throw new ArgumentOutOfRangeException(nameof(tonic));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Tonic = tonic;
        Mode = mode;
    }

    public PitchClass Tonic { get; }

    public KeyMode Mode { get; }

    public int TonicSemitone => (int)Tonic;

    public string Name => SharpNames[TonicSemitone];

    /// <summary>
    /// Код на колесе Кэмелота: например, 8B для C-dur и 5A для A-moll.
    /// </summary>
    public string Camelot => Mode == KeyMode.Major
        ? MajorCamelot[TonicSemitone]
        : MinorCamelot[TonicSemitone];

    public string DisplayName => $"{Name} {Mode.ToString().ToLowerInvariant()}";

    /// <summary>Компактная подпись для карточки трека: "C# min · 12A".</summary>
    public string ShortDisplayName => $"{Name} {ModeToString()} · {Camelot}";

    public override string ToString() => ShortDisplayName;

    private string ModeToString() => Mode == KeyMode.Major ? "maj" : "min";
}

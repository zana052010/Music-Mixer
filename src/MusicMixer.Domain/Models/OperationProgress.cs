// Прогресс длительной операции: доля 0..1 и текст для UI. Слой Domain.
// Используется всеми сервисами и агрегируется в MashupWorkflow.
namespace MusicMixer.Domain.Models;

/// <summary>
/// Крупнозернистый прогресс, который сообщают длительные сервисы.
/// Доля ограничивается диапазоном 0..1, чтобы не сломать индикатор UI.
/// </summary>
public readonly record struct OperationProgress
{
    public OperationProgress(double fraction, string? message = null)
    {
        if (!double.IsFinite(fraction))
        {
            throw new ArgumentOutOfRangeException(nameof(fraction));
        }

        // Math.Clamp защищает UI от инструментов, которые присылают 1.2 или -0.1.
        Fraction = Math.Clamp(fraction, 0, 1);
        Message = message?.Trim() ?? string.Empty;
    }

    public double Fraction { get; }

    public string Message { get; }

    public int Percentage => (int)Math.Round(Fraction * 100, MidpointRounding.AwayFromZero);

    public static OperationProgress Starting(string? message = null) => new(0, message);

    public static OperationProgress Complete(string? message = null) => new(1, message);
}

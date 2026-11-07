// Иерархия исключений предметной области для сбоев аудиоконвейера.
// Слой Domain: не зависит от Infrastructure, UI и внешних инструментов.
// Ловится в MashupWorkflow, ViewModel и сервисах Infrastructure для показа диагностики.
namespace MusicMixer.Domain.Exceptions;

/// <summary>
/// Базовое исключение приложения: позволяет верхним слоям отличать ожидаемые
/// сбои конвейера от непредвиденных ошибок.
/// </summary>
public class MusicMixerException : Exception
{
    public MusicMixerException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Сбой операции аудиоконвейера. Diagnostics хранит ограниченный хвост вывода
/// внешнего инструмента, чтобы UI мог показать причину, не раскрывая
/// неограниченный журнал.
/// </summary>
public sealed class AudioProcessingException : MusicMixerException
{
    public AudioProcessingException(string message, string? diagnostics = null, Exception? innerException = null)
        : base(message, innerException)
    {
        // Обрезаем хвост лога и не храним пустую строку: UI показывает
        // диагностику только когда внешний инструмент действительно что-то сообщил.
        Diagnostics = string.IsNullOrWhiteSpace(diagnostics) ? null : diagnostics.Trim();
    }

    public string? Diagnostics { get; }
}

/// <summary>
/// Возникает, когда внешний исполняемый файл не удаётся запустить
/// (обычно нет в PATH или отсутствует нативная библиотека).
/// </summary>
public sealed class ToolUnavailableException : MusicMixerException
{
    public ToolUnavailableException(string toolName, Exception? innerException = null)
        : base(
            $"External tool '{toolName}' could not be started. Install it and ensure it is on PATH, " +
            "or configure its absolute path in the application options.",
            innerException)
    {
        ToolName = toolName;
    }

    public string ToolName { get; }
}

/// <summary>
/// Анализ файла не дал пригодных BPM, даунбита или тональности.
/// </summary>
public sealed class AudioAnalysisException : MusicMixerException
{
    public AudioAnalysisException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Параметры выравнивания несовместимы: треки не проанализированы или
/// рассчитанные темп и сдвиг выходят за допустимые границы.
/// </summary>
public sealed class AlignmentException : MusicMixerException
{
    public AlignmentException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

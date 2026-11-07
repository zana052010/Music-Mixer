// Контракт запуска внешних процессов. Слой Infrastructure.
// Абстракция нужна, чтобы Demucs, FFmpeg и YouTube-конвертер можно было
// тестировать без реальных бинарников.
namespace MusicMixer.Infrastructure.Processes;

/// <summary>
/// Запрос на запуск: путь к исполняемому файлу, список аргументов и рабочий
/// каталог. Аргументы передаются списком, а не одной строкой — это исключает
/// проблемы с кавычками и инъекции через оболочку.
/// </summary>
public sealed record ProcessLaunchRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null);

/// <summary>Одна строка вывода инструмента; IsError отличает stderr от stdout.</summary>
public sealed record ProcessOutputLine(string Text, bool IsError);

/// <summary>Код возврата плюс ограниченный хвост вывода для диагностики.</summary>
public sealed record ProcessExecutionResult(int ExitCode, string OutputTail);

public interface IProcessRunner
{
    /// <summary>
    /// Запускает внешний процесс без оболочки, одновременно читает оба потока
    /// вывода и убивает всё дерево процессов при отмене.
    /// </summary>
    Task<ProcessExecutionResult> RunAsync(
        ProcessLaunchRequest request,
        Action<ProcessOutputLine>? onOutputLine = null,
        CancellationToken cancellationToken = default);
}

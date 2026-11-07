// Единственная точка запуска CLI-инструментов (Demucs, FFmpeg).
// Слой Infrastructure. Вызывается адаптерами разделения, рендеринга,
// обрезки и загрузки YouTube.
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MusicMixer.Domain.Exceptions;

namespace MusicMixer.Infrastructure.Processes;

/// <summary>
/// Безопасный запуск внешних процессов: без командной оболочки, с
/// одновременным чтением stdout/stderr, отменой через убийство дерева
/// процессов и ограниченным хвостом вывода для сообщений об ошибках.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    // Диагностика хранится ограниченно: длинный лог Demucs/FFmpeg не должен
    // раздувать память и попадать в UI целиком.
    private const int OutputTailLimit = 32 * 1024;

    public async Task<ProcessExecutionResult> RunAsync(
        ProcessLaunchRequest request,
        Action<ProcessOutputLine>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            // UseShellExecute=false обязательно: иначе .NET соберёт командную
            // строку сама, и аргументы с пробелами/кавычками станут опасными.
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // ArgumentList экранирует каждый аргумент отдельно.
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ToolUnavailableException(request.ExecutablePath);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new ToolUnavailableException(request.ExecutablePath, exception);
        }

        var outputTail = new BoundedOutputBuffer(OutputTailLimit);
        // При отмене сначала убиваем процесс, иначе WaitForExitAsync будет
        // ждать, пока инструмент сам решит завершиться.
        using var cancellationRegistration = cancellationToken.Register(
            static state => TryKill((Process)state!),
            process);

        // Оба потока читаются параллельно. Если читать только один, буфер
        // другого переполнится и процесс зависнет на записи в stderr.
        var stdoutPump = PumpAsync(process.StandardOutput, isError: false, onOutputLine, outputTail);
        var stderrPump = PumpAsync(process.StandardError, isError: true, onOutputLine, outputTail);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ждём фактического завершения убитого процесса, чтобы временные
            // файлы не удалялись, пока инструмент ещё держит их открытыми.
            TryKill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Процесс завершился, пока обрабатывалась отмена.
            }

            // Дочитываем насосы, чтобы фоновые исключения не остались
            // незамеченными и не всплыли позже в чужом контексте.
            await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);
            throw;
        }

        await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);
        return new ProcessExecutionResult(process.ExitCode, outputTail.ToString());
    }

    /// <summary>
    /// Построчно читает один поток процесса, складывает строки в ограниченный
    /// хвост и передаёт подписчику. Исключения ввода-вывода при закрытии
    /// потока считаются нормальным завершением, а не ошибкой.
    /// </summary>
    private static async Task PumpAsync(
        StreamReader reader,
        bool isError,
        Action<ProcessOutputLine>? onOutputLine,
        BoundedOutputBuffer outputTail)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                outputTail.Append(line);
                onOutputLine?.Invoke(new ProcessOutputLine(line, isError));
            }
        }
        catch (IOException)
        {
            // Поток закрылся при завершении процесса. Код возврата и
            // сохранённый хвост вывода остаются главным признаком сбоя.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Убивает процесс вместе со всеми дочерними процессами. Demucs и FFmpeg
    /// могут порождать потомков, поэтому одного Kill() недостаточно.
    /// </summary>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (Win32Exception)
        {
            // Access can be denied if the process exited at the same instant.
        }
    }

    /// <summary>
    /// Потокобезопасный буфер, который хранит только последние Limit символов.
    /// Этого достаточно, чтобы показать причину сбоя, но не весь лог.
    /// </summary>
    private sealed class BoundedOutputBuffer
    {
        private readonly StringBuilder _builder = new();
        private readonly object _gate = new();
        private readonly int _limit;

        public BoundedOutputBuffer(int limit)
        {
            _limit = limit;
        }

        public void Append(string line)
        {
            lock (_gate)
            {
                _builder.AppendLine(line);
                if (_builder.Length > _limit)
                {
                    _builder.Remove(0, _builder.Length - _limit);
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _builder.ToString();
            }
        }
    }
}

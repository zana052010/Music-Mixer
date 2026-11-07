// Временные рабочие области для стемов и рендеров. Слой Infrastructure.
// Реализует порт IWorkspace; создаётся один раз на подготовку мэшапа.
using MusicMixer.Application.Abstractions;

namespace MusicMixer.Infrastructure.Workspaces;

/// <summary>
/// Создаёт уникальный каталог в системном temp для одного задания.
/// </summary>
public sealed class TemporaryWorkspaceFactory : IWorkspaceFactory
{
    public IWorkspace Create(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        // Название задания попадает в путь, поэтому оставляем только буквы,
        // цифры и дефисы — так исключаются пробелы, слэши и двоеточия Windows.
        var safePurpose = string.Concat(purpose.Select(
            character => char.IsLetterOrDigit(character) ? character : '-'));
        var directory = Path.Combine(
            Path.GetTempPath(),
            "MusicMixer",
            $"{safePurpose}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");

        return new TemporaryWorkspace(directory);
    }

    /// <summary>
    /// Каталог одного задания. Удаляется при Dispose; если нативные файлы
    /// ещё заняты, ошибка игнорируется и очистку выполнит ОС.
    /// </summary>
    private sealed class TemporaryWorkspace : IWorkspace
    {
        public TemporaryWorkspace(string directoryPath)
        {
            DirectoryPath = Path.GetFullPath(directoryPath);
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }

        public string GetPath(string fileName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            // Абсолютный путь позволил бы записать файл вне рабочей области.
            if (Path.IsPathRooted(fileName))
            {
                throw new ArgumentException("Workspace file names must be relative.", nameof(fileName));
            }

            var fullPath = Path.GetFullPath(Path.Combine(DirectoryPath, fileName));
            var rootWithSeparator = DirectoryPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // Сравнение с завершающим разделителем отсекает и "../secret",
            // и каталог-сосед с похожим именем (C:\Temp\Job2).
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                throw new ArgumentException("The requested path escapes the workspace.", nameof(fileName));
            }

            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            return fullPath;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // Нативный аудиодекодер может ещё держать файл после аварийного
                // завершения; такой каталог позже удалит очистка temp ОС.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

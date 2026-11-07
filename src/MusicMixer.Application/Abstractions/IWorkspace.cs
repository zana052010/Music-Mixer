// Порт временной рабочей области. Каждая подготовка мэшапа получает свой
// каталог для стемов и рендеров; реализация — TemporaryWorkspaceFactory.
namespace MusicMixer.Application.Abstractions;

/// <summary>
/// Временный каталог задания. Освобождается вместе с сессией или при выходе.
/// </summary>
public interface IWorkspace : IDisposable
{
    string DirectoryPath { get; }

    /// <summary>
    /// Возвращает полный путь внутри рабочей области и отклоняет попытки
    /// выйти за её пределы через .. или абсолютный путь.
    /// </summary>
    string GetPath(string fileName);
}

/// <summary>
/// Создаёт уникальную рабочую область под конкретную операцию.
/// </summary>
public interface IWorkspaceFactory
{
    IWorkspace Create(string purpose);
}

// Контракт локализации приложения.
// Слой Desktop; используется ViewModel и code-behind через DI, а XAML — через LocalizeExtension.
// Реализация LocalizationManager хранит словари en/ru и уведомляет об смене языка.
namespace MusicMixer.Desktop.Localization;

/// <summary>
/// Доступ к строкам интерфейса и смена активного языка без пересоздания окон.
/// </summary>
public interface ILocalizationService
{
    /// <summary>
    /// Текущая культура в нормализованном виде: en-US или ru-RU.
    /// </summary>
    string CurrentCulture { get; }

    /// <summary>
    /// Возникает после смены языка, когда все привязки уже обновлены.
    /// </summary>
    event EventHandler? LanguageChanged;

    /// <summary>
    /// Переключает словарь и культуру; повторный вызов с тем же языком ничего не делает.
    /// </summary>
    void SetLanguage(string cultureCode);

    /// <summary>
    /// Возвращает перевод по ключу, английский вариант при отсутствии перевода
    /// или сам ключ, если строки нет ни в одном словаре.
    /// </summary>
    string GetString(string key);

    /// <summary>
    /// Форматирует шаблон по текущей культуре; при ошибке разметки возвращает шаблон без подстановки.
    /// </summary>
    string Format(string key, params object?[] arguments);
}

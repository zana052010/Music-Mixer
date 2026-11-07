// Живая локализация RU/EN без пересоздания окон.
// Слой Desktop, singletons: XAML и ViewModel обращаются к одному менеджеру.
// Словари — встроенные ресурсы Localization/Strings.en.json и Strings.ru.json.
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicMixer.Desktop.Localization;

/// <summary>
/// Синглтон-локализатор с живыми уведомлениями привязок.
///
/// XAML использует расширение LocalizeExtension, которое привязывается к
/// маленьким объектам LocalizedString, принадлежащим менеджеру. Смена языка
/// обновляет их на месте, поэтому все открытые окна меняют язык без
/// пересоздания и без потери состояния навигации.
/// </summary>
public sealed class LocalizationManager : ObservableObject, ILocalizationService
{
    private const string EnglishCulture = "en-US";
    private const string RussianCulture = "ru-RU";

    private readonly Dictionary<string, string> _english;
    private readonly Dictionary<string, string> _russian;
    private readonly Dictionary<string, LocalizedString> _localizedValues =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private Dictionary<string, string> _current;
    private string _currentCulture = EnglishCulture;
    private bool _hasAppliedLanguage;

    private LocalizationManager()
    {
        _english = LoadDictionary("en");
        _russian = LoadDictionary("ru");
        _current = _english;

        // По одному стабильному объекту на ключ. XAML привязывается именно
        // к нему, поэтому смена языка не требует пересоздания представления.
        foreach (var key in _english.Keys)
        {
            _localizedValues[key] = new LocalizedString(this, key);
        }

        // Ключ может быть только в русском словаре (например, новый перевод):
        // создаём объект и для него, чтобы он не потерялся.
        foreach (var key in _russian.Keys)
        {
            _localizedValues.TryAdd(key, new LocalizedString(this, key));
        }
    }

    /// <summary>
    /// Тот же экземпляр, что зарегистрирован в DI. Нужен XAML-расширению,
    /// которое не может получить сервис через конструктор.
    /// </summary>
    public static LocalizationManager Instance { get; } = new();

    public string CurrentCulture
    {
        get => _currentCulture;
        private set => SetProperty(ref _currentCulture, value);
    }

    public string this[string key] => GetString(key);

    public event EventHandler? LanguageChanged;

    public static string GetSystemDefaultCulture() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
            "ru",
            StringComparison.OrdinalIgnoreCase)
            ? RussianCulture
            : EnglishCulture;

    public void SetLanguage(string cultureCode)
    {
        var normalized = NormalizeCulture(cultureCode);
        // Повторный выбор того же языка не должен дёргать все привязки.
        if (_hasAppliedLanguage &&
            string.Equals(CurrentCulture, normalized, StringComparison.Ordinal))
        {
            return;
        }

        var next = normalized == RussianCulture ? _russian : _english;
        var culture = CultureInfo.GetCultureInfo(normalized);
        _current = next;
        CultureInfo.CurrentUICulture = culture;

        // Часть XAML обращается к ресурсам приложения по ключу, а не через
        // LocalizeExtension; обновляем и этот путь.
        if (Avalonia.Application.Current is { } application)
        {
            foreach (var key in next.Keys)
            {
                application.Resources[key] = next[key];
            }
        }

        CurrentCulture = normalized;
        _hasAppliedLanguage = true;

        // Единственное место, где все привязки получают сигнал обновления.
        lock (_gate)
        {
            foreach (var localizedValue in _localizedValues.Values)
            {
                localizedValue.NotifyValueChanged();
            }
        }

        // "Item[]" — стандартное уведомление для привязок вида {Binding [Key]}.
        OnPropertyChanged("Item[]");
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Возвращает перевод по ключу. Если ключа нет в текущем языке,
    /// используется английский вариант, затем сам ключ — UI не остаётся пустым.
    /// </summary>
    public string GetString(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        if (_current.TryGetValue(key, out var value))
        {
            return value;
        }

        if (_english.TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        return key;
    }

    /// <summary>
    /// Форматирует шаблон с учётом текущей культуры. Битый шаблон не должен
    /// ронять UI: при FormatException возвращается исходный текст.
    /// </summary>
    public string Format(string key, params object?[] arguments)
    {
        var template = GetString(key);
        if (arguments.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(
                CultureInfo.GetCultureInfo(CurrentCulture),
                template,
                arguments);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public LocalizedString GetLocalizedValue(string key)
    {
        lock (_gate)
        {
            if (_localizedValues.TryGetValue(key, out var value))
            {
                return value;
            }

            value = new LocalizedString(this, key);
            _localizedValues[key] = value;
            return value;
        }
    }

    private static string NormalizeCulture(string cultureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cultureCode);

        return cultureCode.Trim().ToLowerInvariant() switch
        {
            "en" or "en-us" => EnglishCulture,
            "ru" or "ru-ru" => RussianCulture,
            _ => throw new ArgumentException(
                $"Unsupported culture '{cultureCode}'. Supported cultures: en-US, ru-RU.",
                nameof(cultureCode)),
        };
    }

    /// <summary>
    /// Загружает встроенный JSON-ресурс из сборки. Нестроковые и пустые
    /// значения пропускаются, чтобы словарь всегда был безопасен для UI.
    /// </summary>
    private static Dictionary<string, string> LoadDictionary(string culture)
    {
        var assemblyName = typeof(LocalizationManager).Assembly.GetName().Name
            ?? throw new InvalidOperationException("The application assembly has no name.");
        var uri = new Uri(
            $"avares://{assemblyName}/Localization/Strings.{culture}.json",
            UriKind.Absolute);

        if (!AssetLoader.Exists(uri))
        {
            throw new InvalidOperationException($"Localization asset '{uri}' was not found.");
        }

        using var stream = AssetLoader.Open(uri);
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Localization asset '{uri}' must contain a JSON object.");
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) ||
                property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            normalized[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return normalized;
    }
}

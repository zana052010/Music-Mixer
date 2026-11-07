// Сервис переключения системной, светлой и тёмной темы приложения.
// Слой Desktop; вызывается MainViewModel командой SetTheme.
// Меняет ThemeVariant и просит WindowThemeHelper обновить заголовки открытых окон.
using Avalonia;
using Avalonia.Styling;

namespace MusicMixer.Desktop.Services;

/// <summary>
/// Хранит выбранную тему и применяет её к приложению и нативным заголовкам окон.
/// </summary>
public interface IThemeService
{
    /// <summary>
    /// Текущий режим: System, Light или Dark.
    /// </summary>
    string CurrentTheme { get; }

    /// <summary>
    /// Возникает после фактической смены темы, чтобы ViewModel обновила признаки выбора.
    /// В системном режиме также срабатывает при смене темы операционной системы.
    /// </summary>
    event EventHandler? ThemeChanged;

    /// <summary>
    /// Принимает System, Light или Dark без учёта регистра; неизвестное значение вызывает исключение.
    /// </summary>
    void SetTheme(string theme);
}

/// <summary>
/// Реализация темы поверх Avalonia ThemeVariant с синхронизацией нативной панели Windows.
/// </summary>
public sealed class ThemeService : IThemeService, IDisposable
{
    public const string SystemTheme = "System";
    public const string LightTheme = "Light";
    public const string DarkTheme = "Dark";

    private readonly Avalonia.Application? _application;
    private string _currentTheme;
    private bool _disposed;

    public ThemeService()
    {
        _application = Avalonia.Application.Current;
        // App.axaml выставляет RequestedThemeVariant=Default, поэтому при
        // первом запуске выбран именно системный режим. Если хост заранее
        // задал Light/Dark, сервис уважает это решение.
        _currentTheme = FromVariant(_application?.RequestedThemeVariant);

        if (_application is not null)
        {
            // В системном режиме Avalonia сама меняет ActualThemeVariant при
            // переключении темы ОС. DWM-заголовки нужно обновить отдельно.
            _application.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        }
    }

    public string CurrentTheme => _currentTheme;

    public event EventHandler? ThemeChanged;

    /// <summary>
    /// Применяет тему ко всему приложению и уведомляет подписчиков.
    /// </summary>
    public void SetTheme(string theme)
    {
        var normalized = Normalize(theme);
        // Повторный выбор той же темы не должен слать событие и дёргать DWM.
        if (string.Equals(_currentTheme, normalized, StringComparison.Ordinal))
        {
            return;
        }

        var application = Avalonia.Application.Current
            ?? throw new InvalidOperationException("Avalonia application is not available.");
        application.RequestedThemeVariant = normalized switch
        {
            DarkTheme => ThemeVariant.Dark,
            LightTheme => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
        _currentTheme = normalized;
        // ThemeVariant меняет содержимое, но заголовок окна на Windows рисует DWM отдельно.
        WindowThemeHelper.ApplyToOpenWindows();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_application is not null)
        {
            _application.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        }
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }

        // DWM не знает о ThemeVariant.Default: при смене системной темы
        // заголовки окон нужно перекрасить вручную.
        WindowThemeHelper.ApplyToOpenWindows();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FromVariant(ThemeVariant? variant) => variant switch
    {
        var value when value == ThemeVariant.Dark => DarkTheme,
        var value when value == ThemeVariant.Light => LightTheme,
        _ => SystemTheme,
    };

    private static string Normalize(string theme)
    {
        // CommandParameter из XAML приходит строкой, поэтому регистр и пробелы нормализуются.
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);
        return theme.Trim().ToLowerInvariant() switch
        {
            "system" => SystemTheme,
            "light" => LightTheme,
            "dark" => DarkTheme,
            _ => throw new ArgumentException(
                $"Unknown theme '{theme}'. Supported themes: System, Light, Dark.",
                nameof(theme)),
        };
    }
}

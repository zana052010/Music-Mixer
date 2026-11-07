// Помощник темизации нативной заголовочной панели Windows через DWM.
// Слой Desktop; вызывается ThemeService при смене темы и окнами при открытии/активации.
// Содержимое окон оформляет Avalonia, здесь меняется только системный title bar.
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;

namespace MusicMixer.Desktop.Services;

/// <summary>
/// Applies the current light/dark palette to the native Windows title bar.
/// Content theming is handled by Avalonia theme dictionaries.
/// </summary>
public static class WindowThemeHelper
{
    // 20 — актуальный номер атрибута с Windows 10 20H1, 19 — номер для более старых сборок.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>
    /// Подписывает окно на события открытия и активации, чтобы заголовок
    /// получал актуальную тему после появления нативного handle.
    /// </summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        // Handle существует только после Opened, а при активации DWM может
        // перерисовать панель и потерять ранее выставленный атрибут.
        window.Opened += (_, _) => Apply(window);
        window.Activated += (_, _) => Apply(window);
    }

    /// <summary>
    /// Применяет текущую тему ко всем открытым окнам, включая диалоги.
    /// </summary>
    public static void ApplyToOpenWindows()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        // У каждого окна своя нативная рамка, поэтому обходить нужно все, а не только MainWindow.
        foreach (var window in desktop.Windows)
        {
            Apply(window);
        }
    }

    /// <summary>
    /// Синхронизирует тему окна с приложением и, если возможно, красит title bar.
    /// </summary>
    public static void Apply(Window window)
    {
        var application = Avalonia.Application.Current;
        if (application is not null)
        {
            // Popup roots (tooltips, context menus) follow the owning top level.
            // Копия темы нужна всплывающим слоям: они не всегда наследуют её от окна.
            window.RequestedThemeVariant = application.RequestedThemeVariant;
        }

        if (!OperatingSystem.IsWindows())
        {
            // На других платформах нативной панелью управляет оконный менеджер.
            return;
        }

        try
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero)
            {
                // Окно ещё не имеет нативного handle — попытка повторится при активации.
                return;
            }

            var useDark = (application?.ActualThemeVariant ?? window.ActualThemeVariant) ==
                ThemeVariant.Dark
                ? 1
                : 0;
            var result = DwmSetWindowAttribute(
                handle,
                DwmwaUseImmersiveDarkMode,
                ref useDark,
                sizeof(int));
            if (result != 0)
            {
                // Старые сборки Windows 10 знают только атрибут 19; ошибку игнорируем,
                // потому что оформление заголовка не влияет на работу приложения.
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmwaUseImmersiveDarkModeBefore20H1,
                    ref useDark,
                    sizeof(int));
            }
        }
        catch (Exception)
        {
            // Native title-bar theming is best-effort and platform-specific.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int value,
        int valueSize);
}

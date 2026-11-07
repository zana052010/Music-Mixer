// Определение браузера по умолчанию для fallback-загрузки cookies.
// yt-dlp принимает имя браузера, а не путь к его профилю.
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MusicMixer.Infrastructure.YouTube;

/// <summary>
/// Находит браузер, из которого yt-dlp может взять cookies YouTube.
/// Сначала используется браузер по умолчанию Windows, затем — известные
/// профили установленных браузеров.
/// </summary>
internal static class BrowserCookieLocator
{
    public static string? FindDefaultBrowser()
    {
        if (OperatingSystem.IsWindows())
        {
            var defaultBrowser = FindWindowsDefaultBrowser();
            if (defaultBrowser is not null)
            {
                return defaultBrowser;
            }
        }

        return FindInstalledBrowser();
    }

    [SupportedOSPlatform("windows")]
    private static string? FindWindowsDefaultBrowser()
    {
        try
        {
            // Программная ассоциация https — самый надёжный признак браузера,
            // который пользователь реально открывает.
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (key?.GetValue("ProgId") is not string progId || progId.Length == 0)
            {
                return null;
            }

            if (progId.Contains("ChromeHTML", StringComparison.OrdinalIgnoreCase))
            {
                return "chrome";
            }

            if (progId.Contains("MSEdge", StringComparison.OrdinalIgnoreCase))
            {
                return "edge";
            }

            if (progId.Contains("Firefox", StringComparison.OrdinalIgnoreCase))
            {
                return "firefox";
            }

            if (progId.Contains("Brave", StringComparison.OrdinalIgnoreCase))
            {
                return "brave";
            }

            if (progId.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase))
            {
                return "vivaldi";
            }

            if (progId.Contains("Opera", StringComparison.OrdinalIgnoreCase))
            {
                return "opera";
            }

            if (progId.Contains("Chromium", StringComparison.OrdinalIgnoreCase))
            {
                return "chromium";
            }
        }
        catch (Exception)
        {
            // Реестр может быть недоступен в ограниченном окружении;
            // ниже остаётся поиск по профилям.
        }

        return null;
    }

    private static string? FindInstalledBrowser()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var candidates = new (string Browser, string[] Paths)[]
        {
            ("firefox", OperatingSystem.IsWindows()
                ? [Path.Combine(roaming, "Mozilla", "Firefox", "Profiles")]
                : OperatingSystem.IsMacOS()
                    ? [Path.Combine(home, "Library", "Application Support", "Firefox", "Profiles")]
                    : [Path.Combine(home, ".mozilla", "firefox")]),
            ("chrome", OperatingSystem.IsWindows()
                ? [Path.Combine(local, "Google", "Chrome", "User Data")]
                : OperatingSystem.IsMacOS()
                    ? [Path.Combine(home, "Library", "Application Support", "Google", "Chrome")]
                    : [Path.Combine(home, ".config", "google-chrome")]),
            ("edge", OperatingSystem.IsWindows()
                ? [Path.Combine(local, "Microsoft", "Edge", "User Data")]
                : OperatingSystem.IsMacOS()
                    ? [Path.Combine(home, "Library", "Application Support", "Microsoft Edge")]
                    : [Path.Combine(home, ".config", "microsoft-edge")]),
            ("brave", OperatingSystem.IsWindows()
                ? [Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")]
                : OperatingSystem.IsMacOS()
                    ? [Path.Combine(home, "Library", "Application Support", "BraveSoftware", "Brave-Browser")]
                    : [Path.Combine(home, ".config", "BraveSoftware", "Brave-Browser")]),
            ("vivaldi", OperatingSystem.IsWindows()
                ? [Path.Combine(local, "Vivaldi", "User Data")]
                : OperatingSystem.IsMacOS()
                    ? [Path.Combine(home, "Library", "Application Support", "Vivaldi")]
                    : [Path.Combine(home, ".config", "vivaldi")]),
            ("opera", OperatingSystem.IsWindows()
                ? [Path.Combine(roaming, "Opera Software", "Opera Stable")]
                : OperatingSystem.IsMacOS()
                    ? [Path.Combine(home, "Library", "Application Support", "com.operasoftware.Opera")]
                    : [Path.Combine(home, ".config", "opera")]),
        };

        foreach (var (browser, paths) in candidates)
        {
            if (paths.Any(Directory.Exists))
            {
                return browser;
            }
        }

        return null;
    }
}

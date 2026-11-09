// Точка входа настольного приложения MusicMixer.
// Слой Desktop: поднимает Avalonia и передаёт ей управление.
// Вызывается CLR при старте MusicMixer.exe; далее создаёт App и MainWindow.
using Avalonia;
using MusicMixer.Infrastructure.YouTube;

namespace MusicMixer.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // До запуска UI: снимаем региональную блокировку YoutubeExplode (см. YoutubeExplodeRegionShim).
        YoutubeExplodeRegionShim.Initialize();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Собирает конфигурацию Avalonia: платформенный бэкенд, шрифт Inter и
    /// вывод логов в Trace. Метод также находит дизайнер Avalonia.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

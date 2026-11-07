// Сервис показа модального окна YouTube. Слой Desktop.
// Создаёт ViewModel из DI, связывает диалог с главным окном и
// отменяет загрузку при отмене вызывающего токена.
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MusicMixer.Desktop.Views;

namespace MusicMixer.Desktop.YouTube;

public sealed class YouTubeSearchDialogService : IYouTubeDialogService
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly IServiceProvider _services;

    public YouTubeSearchDialogService(
        IClassicDesktopStyleApplicationLifetime lifetime,
        IServiceProvider services)
    {
        _lifetime = lifetime;
        _services = services;
    }

    public async Task<YouTubeIngestionResult?> ShowAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var owner = _lifetime.MainWindow
            ?? throw new InvalidOperationException("The main window is not available.");
        // ViewModel — transient из DI: у каждого открытия диалога своё состояние.
        var viewModel = _services.GetRequiredService<YouTubeSearchViewModel>();
        viewModel.Initialize(outputDirectory);
        var dialog = new YouTubeSearchDialog(viewModel);

        // Внешняя отмена (например, закрытие приложения) должна закрыть окно
        // в UI-потоке, а не из фонового.
        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var window = (YouTubeSearchDialog)state!;
                Dispatcher.UIThread.Post(() =>
                {
                    if (window.IsVisible)
                    {
                        window.Close();
                    }
                });
            },
            dialog);

        try
        {
            // ShowDialog блокирует только вызывающий async-метод и
            // возвращает результат, когда окно закрыто.
            return await dialog.ShowDialog<YouTubeIngestionResult?>(owner);
        }
        catch
        {
            // Если окно не открылось, ViewModel уже создана и держит ресурсы.
            viewModel.Dispose();
            throw;
        }
    }
}

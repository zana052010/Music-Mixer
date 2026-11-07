// Сервис показа модального окна обрезки. Слой Desktop.
// Инициализирует AudioTrimViewModel (загружает источник в плеер),
// показывает окно и возвращает выбранный диапазон или null.
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MusicMixer.Desktop.Views;

namespace MusicMixer.Desktop.Trimming;

public sealed class AudioTrimDialogService : IAudioTrimDialogService
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly IServiceProvider _services;

    public AudioTrimDialogService(
        IClassicDesktopStyleApplicationLifetime lifetime,
        IServiceProvider services)
    {
        _lifetime = lifetime;
        _services = services;
    }

    public async Task<AudioTrimSelection?> ShowAsync(
        AudioTrimRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = _lifetime.MainWindow
            ?? throw new InvalidOperationException("The main window is not available.");
        var viewModel = _services.GetRequiredService<AudioTrimViewModel>();

        try
        {
            // Загрузка исходника в BASS может занять время, поэтому
            // выполняется до показа окна и с поддержкой отмены.
            await viewModel.InitializeAsync(request, cancellationToken);
            var dialog = new AudioTrimDialog(viewModel);
            using var cancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var window = (AudioTrimDialog)state!;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (window.IsVisible)
                        {
                            window.Close();
                        }
                    });
                },
                dialog);

            return await dialog.ShowDialog<AudioTrimSelection?>(owner);
        }
        catch
        {
            viewModel.Dispose();
            throw;
        }
    }
}

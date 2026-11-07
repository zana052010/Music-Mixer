// Desktop-слой: диалоговый сервис прослушивания аудио перед добавлением в мэшап.
// Создаёт модальное окно AudioAuditionDialog и изолирует работу с окном от view model.
// Вызывается из MainViewModel; сам вызывает AudioAuditionViewModel и ShowDialog.
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MusicMixer.Desktop.Views;

namespace MusicMixer.Desktop.Audition;

/// <summary>
/// Открывает модальный диалог прослушивания: готовит view model, показывает окно
/// с владельцем и возвращает решение пользователя (добавить или отменить).
/// </summary>
public sealed class AudioAuditionDialogService : IAudioAuditionDialogService
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly IServiceProvider _services;

    public AudioAuditionDialogService(
        IClassicDesktopStyleApplicationLifetime lifetime,
        IServiceProvider services)
    {
        _lifetime = lifetime;
        _services = services;
    }

    /// <summary>
    /// Готовит и показывает модальное окно; false возвращается при отмене,
    /// закрытии крестиком или срабатывании токена.
    /// </summary>
    public async Task<bool> ShowAsync(
        AudioAuditionRequest request,
        CancellationToken cancellationToken = default)
    {
        // Ранняя отмена не должна даже создавать окно и загружать файл в BASS.
        cancellationToken.ThrowIfCancellationRequested();
        // Без главного окна модальный показ невозможен — это ошибка конфигурации приложения.
        var owner = _lifetime.MainWindow
            ?? throw new InvalidOperationException("The main window is not available.");
        // VM зарегистрирована как transient: каждый вызов получает чистое состояние плеера.
        var viewModel = _services.GetRequiredService<AudioAuditionViewModel>();

        try
        {
            // Источник грузим до показа окна, чтобы битый файл не открывал пустой диалог.
            await viewModel.InitializeAsync(request, cancellationToken);
            var dialog = new AudioAuditionDialog(viewModel);
            // Close затрагивает UI-поток, а токен может сработать из фона — закрываем через Dispatcher.
            using var cancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var window = (AudioAuditionDialog)state!;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (window.IsVisible)
                        {
                            window.Close(false);
                        }
                    });
                },
                dialog);

            return await dialog.ShowDialog<bool>(owner);
        }
        catch
        {
            // Если окно так и не открылось, Closed не сработает и VM останется с живым таймером.
            viewModel.Dispose();
            throw;
        }
    }
}

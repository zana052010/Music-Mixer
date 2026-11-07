// Code-behind окна прослушивания перед добавлением трека.
// Слой Desktop: связывает окно с AudioAuditionViewModel и освобождает её
// ресурсы при закрытии.
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MusicMixer.Desktop.Audition;
using MusicMixer.Desktop.Services;

namespace MusicMixer.Desktop.Views;

public partial class AudioAuditionDialog : Window
{
    private readonly AudioAuditionViewModel? _viewModel;

    public AudioAuditionDialog()
    {
        InitializeComponent();
        WindowThemeHelper.Attach(this);
    }

    public AudioAuditionDialog(AudioAuditionViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        // ViewModel не знает об Avalonia Window: она просит закрыться,
        // а окно решает, вернуть true (добавить) или false (отмена).
        viewModel.CloseRequested += OnCloseRequested;
        Closed += OnClosed;

        AuditionSeekSlider.AddHandler(
            PointerPressedEvent,
            OnSeekPointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        AuditionSeekSlider.AddHandler(
            PointerReleasedEvent,
            OnSeekPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        AuditionSeekSlider.AddHandler(
            PointerCaptureLostEvent,
            OnSeekPointerCaptureLost,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void OnCloseRequested(bool confirmed) => Close(confirmed);

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs eventArgs) =>
        _viewModel?.BeginPlayheadSeek();

    private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs eventArgs) =>
        _viewModel?.EndPlayheadSeek();

    private void OnSeekPointerCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs) =>
        _viewModel?.EndPlayheadSeek();

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is null)
        {
            return;
        }

        // Отписываемся и освобождаем BASS-поток прослушивания.
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.Dispose();
    }
}

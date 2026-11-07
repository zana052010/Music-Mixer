// Code-behind окна обрезки источника.
// Слой Desktop: передаёт запрос в AudioTrimViewModel, возвращает выбранный
// диапазон и освобождает плеер при закрытии.
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MusicMixer.Desktop.Services;
using MusicMixer.Desktop.Trimming;

namespace MusicMixer.Desktop.Views;

public partial class AudioTrimDialog : Window
{
    private readonly AudioTrimViewModel? _viewModel;

    public AudioTrimDialog()
    {
        InitializeComponent();
        WindowThemeHelper.Attach(this);
    }

    public AudioTrimDialog(AudioTrimViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        // Результат диалога — AudioTrimSelection или null при отмене.
        viewModel.CloseRequested += OnCloseRequested;
        Closed += OnClosed;

        TrimSeekSlider.AddHandler(
            PointerPressedEvent,
            OnSeekPointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        TrimSeekSlider.AddHandler(
            PointerReleasedEvent,
            OnSeekPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        TrimSeekSlider.AddHandler(
            PointerCaptureLostEvent,
            OnSeekPointerCaptureLost,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void OnCloseRequested(AudioTrimSelection? selection) => Close(selection);

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

        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.Dispose();
    }
}

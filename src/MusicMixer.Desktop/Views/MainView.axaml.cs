// Code-behind главного экрана: только события, которые неудобно выразить
// в MVVM — drag&drop файлов и пауза перемотки во время перетаскивания.
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MusicMixer.Desktop.ViewModels;

namespace MusicMixer.Desktop.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        // Дорожки A и B принимают файлы независимо; Role определяется по Tag.
        TrackACard.AddHandler(DragDrop.DragOverEvent, OnTrackDragOver);
        TrackACard.AddHandler(DragDrop.DropEvent, OnTrackDrop);
        TrackBCard.AddHandler(DragDrop.DragOverEvent, OnTrackDragOver);
        TrackBCard.AddHandler(DragDrop.DropEvent, OnTrackDrop);

        // Перемотка: пока пользователь держит ползунок, воспроизведение
        // приостанавливается, а после отпускания продолжается с новой позиции.
        PlaybackSeekSlider.AddHandler(
            PointerPressedEvent,
            OnPlaybackSeekPointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        PlaybackSeekSlider.AddHandler(
            PointerReleasedEvent,
            OnPlaybackSeekPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        PlaybackSeekSlider.AddHandler(
            PointerCaptureLostEvent,
            OnPlaybackSeekPointerCaptureLost,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void OnTrackDragOver(object? sender, DragEventArgs eventArgs)
    {
        // Курсор показывает «копировать» только если действительно тянут файлы.
        var hasFiles = eventArgs.DataTransfer.TryGetFiles()?.Length > 0;
        eventArgs.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private async void OnTrackDrop(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.Handled = true;

        var file = eventArgs.DataTransfer.TryGetFiles()?
            .OfType<IStorageFile>()
            .FirstOrDefault();
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        // Tag="A" или "B" задан на карточке в XAML.
        var isTrackA = sender is Control control &&
            string.Equals(control.Tag as string, "A", StringComparison.Ordinal);

        await viewModel.SetTrackAsync(isTrackA, path);
    }

    private void OnPlaybackSeekPointerPressed(
        object? sender,
        PointerPressedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.BeginPlaybackSeek();
        }
    }

    private void OnPlaybackSeekPointerReleased(
        object? sender,
        PointerReleasedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.EndPlaybackSeek();
        }
    }

    private void OnPlaybackSeekPointerCaptureLost(
        object? sender,
        PointerCaptureLostEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.EndPlaybackSeek();
        }
    }
}

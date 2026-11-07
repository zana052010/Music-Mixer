// Code-behind окна поиска YouTube.
// Слой Desktop: фокус в поле поиска, Enter, закрытие с результатом
// YouTubeIngestionResult и освобождение ViewModel.
using Avalonia.Controls;
using Avalonia.Input;
using MusicMixer.Desktop.Services;
using MusicMixer.Desktop.YouTube;

namespace MusicMixer.Desktop.Views;

public partial class YouTubeSearchDialog : Window
{
    private readonly YouTubeSearchViewModel? _viewModel;

    public YouTubeSearchDialog()
    {
        InitializeComponent();
        WindowThemeHelper.Attach(this);
    }

    public YouTubeSearchDialog(YouTubeSearchViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        Closed += OnClosed;
        // Пользователь сразу может печатать запрос, без клика по полю.
        Opened += (_, _) => SearchBox.Focus();
    }

    private void OnCloseRequested(YouTubeIngestionResult? result) => Close(result);

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.Dispose();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        // Enter запускает поиск немедленно, минуя debounce из ViewModel.
        if (eventArgs.Key != Key.Enter || _viewModel is null)
        {
            return;
        }

        if (_viewModel.SearchCommand.CanExecute(null))
        {
            _viewModel.SearchCommand.Execute(null);
            eventArgs.Handled = true;
        }
    }
}

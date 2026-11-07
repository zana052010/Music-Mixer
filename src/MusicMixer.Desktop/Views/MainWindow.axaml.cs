// Тонкая оболочка главного окна: создаёт содержимое и подключает тему.
// Слой Desktop; DataContext — MainViewModel, зарегистрированная в DI.
using Avalonia.Controls;
using MusicMixer.Desktop.Services;
using MusicMixer.Desktop.ViewModels;

namespace MusicMixer.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Синхронизирует светлую/тёмную тему с нативной заголовочной панелью.
        WindowThemeHelper.Attach(this);
    }

    public MainWindow(MainViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }
}

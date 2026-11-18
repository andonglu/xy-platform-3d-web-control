using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MotionControl.Wpf.ViewModels;

namespace MotionControl.Wpf;

public partial class MainWindow : Window
{
    private ScriptsWindow? _scriptsWindow;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    { if (ViewModel is not null) await ViewModel.InitializeAsync(); }

    private void JogStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button) ViewModel?.JogCommand.Execute(button.Tag);
        e.Handled = true;
    }
    private void JogStop(object sender, MouseEventArgs e)
    { if (sender is Button button) ViewModel?.StopJogCommand.Execute(button.Tag); }

    private void OpenScriptsWindow(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (_scriptsWindow is null)
        {
            _scriptsWindow = new ScriptsWindow { Owner = this, DataContext = ViewModel.Scripts };
            _scriptsWindow.Closed += (_, _) => _scriptsWindow = null;
        }
        _scriptsWindow.Show();
        _scriptsWindow.Activate();
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => ViewModel?.Dispose();
}

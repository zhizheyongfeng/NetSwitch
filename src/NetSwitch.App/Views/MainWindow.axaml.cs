using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using NetSwitch.App.ViewModels;

namespace NetSwitch.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (App.AllowExit)
        {
            return;
        }

        var closeToTray = (DataContext as MainWindowViewModel)?.CloseToTray ?? false;
        if (closeToTray && App.Tray is { IsAvailable: true })
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void TitleBar_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        ToggleMaximize();
    }

    private void Minimize_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ToggleMaximize();

    private void Close_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using NetSwitch.App.ViewModels;
using NetSwitch.Core.Models;

namespace NetSwitch.App.Views;

public partial class ApplyConfirmationWindow : Window
{
    public ApplyConfirmationWindow()
    {
        InitializeComponent();
    }

    public ApplyConfirmationWindow(
        AdapterInfo adapter,
        Preset preset,
        AdapterState? state,
        bool isSimulated,
        bool isOnlyConnectedAdapter = false)
        : this()
    {
        DataContext = new ApplyConfirmationViewModel(
            adapter, preset, state, isSimulated, isOnlyConnectedAdapter);
    }

    private void Confirm_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(true);

    private void Cancel_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);

    private void Header_OnPointerPressed(object? sender, PointerPressedEventArgs e)
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}

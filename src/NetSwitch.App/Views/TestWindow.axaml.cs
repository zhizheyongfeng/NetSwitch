using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using NetSwitch.App.ViewModels;

namespace NetSwitch.App.Views;

public partial class TestWindow : Window
{
    public TestWindow()
    {
        InitializeComponent();
    }

    public TestWindow(TestWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Opened += async (_, _) => await viewModel.InitializeAsync();
    }

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
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}

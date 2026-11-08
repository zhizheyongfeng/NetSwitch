using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NetSwitch.App.Services;

namespace NetSwitch.App.Views;

public partial class MessageWindow : Window
{
    private static readonly Geometry InfoGeometry = Geometry.Parse(
        "M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z M13,17 L11,17 L11,11 L13,11 Z M13,9 L11,9 L11,7 L13,7 Z");
    private static readonly Geometry SuccessGeometry = Geometry.Parse(
        "M9,16.17 L4.83,12 L3.41,13.41 L9,19 L21,7 L19.59,5.59 Z");
    private static readonly Geometry WarningGeometry = Geometry.Parse(
        "M1,21 L23,21 L12,2 Z M13,18 L11,18 L11,16 L13,16 Z M13,14 L11,14 L11,10 L13,10 Z");
    private static readonly Geometry ErrorGeometry = Geometry.Parse(
        "M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z M13,17 L11,17 L11,15 L13,15 Z M13,13 L11,13 L11,7 L13,7 Z");

    public MessageWindow()
    {
        InitializeComponent();
    }

    public MessageWindow(
        string title,
        string message,
        MessageKind kind,
        string confirmText = "知道了",
        string? cancelText = null,
        bool dangerous = false)
        : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;

        var (geometry, foreground, background) = kind switch
        {
            MessageKind.Success => (SuccessGeometry, "#047857", "#ECFDF5"),
            MessageKind.Warning => (WarningGeometry, "#B45309", "#FFFBEB"),
            MessageKind.Error => (ErrorGeometry, "#B91C1C", "#FEF2F2"),
            _ => (InfoGeometry, "#1D4ED8", "#EFF6FF")
        };

        IconPath.Data = geometry;
        IconPath.Fill = new SolidColorBrush(Color.Parse(foreground));
        IconBadge.Background = new SolidColorBrush(Color.Parse(background));

        if (cancelText is not null)
        {
            CancelButton.Content = cancelText;
            CancelButton.IsVisible = true;
        }

        if (dangerous)
        {
            ConfirmButton.Classes.Remove("primary");
            ConfirmButton.Classes.Add("danger");
        }
    }

    private void Confirm_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(true);

    private void Cancel_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);

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

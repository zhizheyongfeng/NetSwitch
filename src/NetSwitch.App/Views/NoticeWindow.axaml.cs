using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace NetSwitch.App.Views;

/// <summary>
/// Lightweight bottom-right notice used by tray-triggered applies. Avalonia's TrayIcon has
/// no system notification API, so this stays in-process and works while the main window is
/// hidden.
/// </summary>
public partial class NoticeWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public NoticeWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PositionBottomRight();
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Close();
        };
    }

    public NoticeWindow(string message, bool isError)
        : this()
    {
        MessageText.Text = message;
        if (isError)
        {
            TitleText.Text = "操作失败";
            IconPath.Data = Geometry.Parse(
                "M12,2 C6.48,2 2,6.48 2,12 C2,17.52 6.48,22 12,22 C17.52,22 22,17.52 22,12 C22,6.48 17.52,2 12,2 Z M13,17 L11,17 L11,15 L13,15 Z M13,13 L11,13 L11,7 L13,7 Z");
            IconPath.Fill = new SolidColorBrush(Color.Parse("#B91C1C"));
            IconBadge.Background = new SolidColorBrush(Color.Parse("#FEF2F2"));
        }

        _timer.Start();
    }

    private void PositionBottomRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var width = (int)Bounds.Width;
        var height = (int)Bounds.Height;
        Position = new PixelPoint(
            area.X + Math.Max(0, area.Width - width - 16),
            area.Y + Math.Max(0, area.Height - height - 16));
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}

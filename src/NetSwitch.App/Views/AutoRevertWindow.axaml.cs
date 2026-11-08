using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NetSwitch.Core.Abstractions;

namespace NetSwitch.App.Views;

/// <summary>
/// Countdown shown after a dangerous apply. Closing with “保持应用” keeps the new
/// configuration; “立即恢复”, the countdown reaching zero, or pressing Escape reverts.
/// Escape is deliberately the safe action: losing the connection is worse than an
/// unwanted rollback.
/// </summary>
public partial class AutoRevertWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _probeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _probeCancellation = new();
    private readonly IConnectivityProbe? _probe;
    private readonly string? _gateway;
    private int _remaining;
    private bool _decided;
    private bool _probing;

    public AutoRevertWindow()
    {
        InitializeComponent();
        _timer.Tick += OnTick;
        _probeTimer.Tick += async (_, _) => await ProbeGatewayAsync();
    }

    public AutoRevertWindow(
        string presetName,
        int seconds,
        string? gatewayAddress = null,
        IConnectivityProbe? probe = null)
        : this()
    {
        PresetText.Text = $"已应用「{presetName}」";
        _remaining = Math.Max(1, seconds);
        _gateway = gatewayAddress;
        _probe = probe;
        UpdateCountdown();
        _timer.Start();

        if (!string.IsNullOrWhiteSpace(_gateway) && _probe is not null)
        {
            GatewayStatusText.Text = $"正在探测网关 {_gateway}…";
            _probeTimer.Start();
            _ = ProbeGatewayAsync();
        }
    }

    private async Task ProbeGatewayAsync()
    {
        if (_probing || _probe is null || string.IsNullOrWhiteSpace(_gateway))
        {
            return;
        }

        _probing = true;
        try
        {
            var result = await _probe.PingAsync(_gateway, 800, _probeCancellation.Token);
            GatewayStatusText.Text = result.Success
                ? $"网关 {_gateway} 可达（{result.Latency.TotalMilliseconds:0} ms）"
                : $"网关 {_gateway} 暂不可达，仅供参考；不会因此自动回滚";
            GatewayStatusText.Foreground = result.Success
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#059669"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B45309"));
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
        catch
        {
            GatewayStatusText.Text = "无法探测网关（不影响倒计时）";
        }
        finally
        {
            _probing = false;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            _decided = true;
            Close(true);
            return;
        }

        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        CountdownText.Text = _remaining.ToString();
        CountdownCaption.Text = "秒内保持，否则自动恢复到应用前配置";
    }

    private void Keep_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _timer.Stop();
        _decided = true;
        Close(false);
    }

    private void Revert_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _timer.Stop();
        _decided = true;
        Close(true);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _timer.Stop();
            _decided = true;
            Close(true);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Alt+F4 or any platform close that bypasses the buttons is treated as "revert":
        // an unattended dangerous configuration should not be kept by accident.
        if (!_decided)
        {
            _timer.Stop();
            _decided = true;
            e.Cancel = true;
            Close(true);
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _probeTimer.Stop();
        _probeCancellation.Cancel();
        _probeCancellation.Dispose();
        base.OnClosed(e);
    }
}

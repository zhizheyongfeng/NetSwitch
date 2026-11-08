using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using NetSwitch.App.ViewModels;
using NetSwitch.App.Views;
using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;

namespace NetSwitch.App.Services;

/// <summary>
/// System tray icon and menu. The menu is rebuilt from the live adapter/preset list every
/// time the platform asks for it, so check marks and enable states never go stale.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Application _app;
    private readonly MainWindow _window;
    private readonly MainWindowViewModel _viewModel;
    private TrayIcon? _icon;
    private NativeMenu? _menu;
    private NoticeWindow? _notice;

    public TrayService(Application app, MainWindow window, MainWindowViewModel viewModel)
    {
        _app = app;
        _window = window;
        _viewModel = viewModel;
    }

    public bool IsAvailable { get; private set; }

    public bool Attach()
    {
        if (IsAvailable)
        {
            return true;
        }

        try
        {
            var icon = new TrayIcon
            {
                ToolTipText = "NetSwitch 网络配置切换器",
                Icon = LoadIcon(),
                IsVisible = true
            };

            var menu = new NativeMenu();
            menu.NeedsUpdate += (_, _) => RebuildMenu();
            icon.Menu = menu;
            icon.Clicked += (_, _) => ShowMainWindow();

            TrayIcon.SetIcons(_app, new TrayIcons { icon });
            _icon = icon;
            _menu = menu;
            _viewModel.NoticeRequested += OnNoticeRequested;
            _viewModel.Adapters.CollectionChanged += OnAdaptersChanged;
            IsAvailable = true;
            RebuildMenu();
            return true;
        }
        catch
        {
            // No tray host (some Linux sessions, locked-down Windows). Closing the main
            // window then exits normally, which is the safe fallback.
            Dispose();
            return false;
        }
    }

    public void ShowMainWindow()
    {
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void Notify(string message, bool isError)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _notice?.Close();
                var notice = new NoticeWindow(message, isError);
                _notice = notice;
                notice.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_notice, notice))
                    {
                        _notice = null;
                    }
                };
                notice.Show();
            }
            catch
            {
                // Notifications are best-effort; never break the apply flow.
            }
        });
    }

    private void OnNoticeRequested(string message, bool isError) => Notify(message, isError);

    private void OnAdaptersChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(RebuildMenu);

    private void RebuildMenu()
    {
        if (_menu is not null)
        {
            _menu.Items.Clear();

            foreach (var adapter in _viewModel.Adapters)
            {
                var adapterItem = new NativeMenuItem(adapter.Name)
                {
                    Menu = BuildAdapterMenu(adapter)
                };
                _menu.Items.Add(adapterItem);
            }

            if (_viewModel.Adapters.Count == 0)
            {
                _menu.Items.Add(new NativeMenuItem("没有可用的网络适配器") { IsEnabled = false });
            }

            _menu.Items.Add(new NativeMenuItemSeparator());

            var restore = new NativeMenuItem("恢复上次配置")
            {
                IsEnabled = _viewModel.HasLastRecovery
            };
            restore.Click += (_, _) =>
            {
                ShowMainWindow();
                _viewModel.RestoreLastCommand.Execute(null);
            };
            _menu.Items.Add(restore);

            var refresh = new NativeMenuItem("刷新适配器");
            refresh.Click += (_, _) => _viewModel.RefreshCommand.Execute(null);
            _menu.Items.Add(refresh);

            _menu.Items.Add(new NativeMenuItemSeparator());

            var open = new NativeMenuItem("打开主窗口");
            open.Click += (_, _) => ShowMainWindow();
            _menu.Items.Add(open);

            var settings = new NativeMenuItem("设置");
            settings.Click += (_, _) =>
            {
                ShowMainWindow();
                _viewModel.OpenSettingsCommand.Execute(null);
            };
            _menu.Items.Add(settings);

            _menu.Items.Add(new NativeMenuItemSeparator());

            var exit = new NativeMenuItem("退出");
            exit.Click += (_, _) => Exit();
            _menu.Items.Add(exit);
        }
    }

    private NativeMenu BuildAdapterMenu(AdapterItemViewModel adapter)
    {
        var menu = new NativeMenu();
        var presets = _viewModel.GetPresetsFor(adapter.Key);
        if (presets.Count == 0)
        {
            menu.Items.Add(new NativeMenuItem("尚无预设") { IsEnabled = false });
            return menu;
        }

        foreach (var preset in presets)
        {
            var isCurrent = adapter.State is not null &&
                            PresetFingerprint.Matches(preset.Desired, adapter.State);
            var item = new NativeMenuItem(preset.Name)
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = isCurrent
            };
            item.Click += async (_, _) =>
            {
                try
                {
                    await _viewModel.ApplyPresetFromTrayAsync(adapter, preset);
                }
                catch (Exception ex)
                {
                    Notify($"应用「{preset.Name}」失败：{ex.Message}", true);
                }
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    private void Exit()
    {
        App.AllowExit = true;
        if (_app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://NetSwitch/Assets/netswitch.png"));
        return new WindowIcon(stream);
    }

    public void Dispose()
    {
        _viewModel.NoticeRequested -= OnNoticeRequested;
        _viewModel.Adapters.CollectionChanged -= OnAdaptersChanged;
        _notice?.Close();
        _notice = null;

        if (_icon is not null)
        {
            try
            {
                TrayIcon.SetIcons(_app, new TrayIcons());
            }
            catch
            {
                // ignore
            }

            _icon.Dispose();
            _icon = null;
        }

        _menu = null;
        IsAvailable = false;
    }
}

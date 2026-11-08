using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using NetSwitch.App.Services;
using NetSwitch.App.ViewModels;
using NetSwitch.App.Views;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Storage;
using NetSwitch.Core.Validation;
using NetSwitch.Platform.Fake;
using NetSwitch.Platform.Windows;

namespace NetSwitch.App;

public partial class App : Application
{
    /// <summary>The main window; used by the single-instance activation handler.</summary>
    public static Window? MainWindow { get; private set; }

    /// <summary>Set before a deliberate exit so closing the window is not intercepted.</summary>
    public static bool AllowExit { get; set; }

    public static TrayService? Tray { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            ConfigureServices(services);

            var provider = services.BuildServiceProvider();
            var dialogs = (DialogService)provider.GetRequiredService<IDialogService>();
            var viewModel = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = viewModel };

            dialogs.Owner = window;
            desktop.MainWindow = window;
            MainWindow = window;

            var tray = new TrayService(this, window, viewModel);
            tray.Attach();
            Tray = tray;
            desktop.Exit += (_, _) => tray.Dispose();

            window.Opened += async (_, _) =>
            {
                await viewModel.InitializeAsync();
                ApplyTheme(provider.GetRequiredService<IConfigService>().Config.Settings.Theme);
                if (viewModel.StartMinimized && tray.IsAvailable)
                {
                    window.Hide();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ApplyTheme(string theme)
    {
        if (Current is { } app)
        {
            app.RequestedThemeVariant = string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Default
                : ThemeVariant.Light;
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(_ => StoragePathService.CreateDefault());
        services.AddSingleton<IPathService>(sp => sp.GetRequiredService<StoragePathService>());
        services.AddSingleton<ConfigService>();
        services.AddSingleton<IConfigService>(sp => sp.GetRequiredService<ConfigService>());
        services.AddSingleton<IAppLog, FileAppLog>();
        services.AddSingleton<SnapshotStore>();

        if (OperatingSystem.IsWindows())
        {
            ConfigureWindows(services);
        }
        else
        {
            ConfigureSimulated(services);
        }

        services.AddSingleton<IStaticChecker, StaticCheckService>();
        services.AddSingleton<IPresetTester, PresetTester>();

        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<MainWindowViewModel>();
    }

    private static void ConfigureWindows(IServiceCollection services)
    {
        services.AddSingleton<IAdapterCatalog, WindowsAdapterCatalog>();
        services.AddSingleton<INetworkConfigurator, WindowsNetworkConfigurator>();
        services.AddSingleton<IWirelessController, WindowsWirelessController>();
        services.AddSingleton<IConnectivityProbe, WindowsConnectivityProbe>();
        services.AddSingleton<IPrivilegeService, WindowsPrivilegeService>();
        services.AddSingleton<IStartupService, WindowsStartupService>();
        services.AddSingleton<IPlatformCapabilities, WindowsPlatformCapabilities>();
    }

    private static void ConfigureSimulated(IServiceCollection services)
    {
        // The in-memory backend keeps the UI usable on non-Windows hosts (design previews,
        // tests, and Linux/macOS development before M6).
        services.AddSingleton(_ => FakeNetworkState.CreateDefault());
        services.AddSingleton<IAdapterCatalog, FakeAdapterCatalog>();
        services.AddSingleton<INetworkConfigurator, FakeNetworkConfigurator>();
        services.AddSingleton<IWirelessController, FakeWirelessController>();
        services.AddSingleton<IConnectivityProbe, FakeConnectivityProbe>();
        services.AddSingleton<IPrivilegeService, FakePrivilegeService>();
        services.AddSingleton<IStartupService, FakeStartupService>();
        services.AddSingleton<IPlatformCapabilities, FakePlatformCapabilities>();
    }
}

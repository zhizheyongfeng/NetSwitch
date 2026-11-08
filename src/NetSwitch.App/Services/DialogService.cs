using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NetSwitch.App.ViewModels;
using NetSwitch.App.Views;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;
using NetSwitch.Core.Storage;

namespace NetSwitch.App.Services;

public sealed class DialogService : IDialogService
{
    private readonly IPresetTester _tester;
    private readonly IConfigService _config;
    private readonly StoragePathService _paths;
    private readonly IPlatformCapabilities _capabilities;
    private readonly IPrivilegeService _privileges;
    private readonly IStartupService _startup;
    private readonly IConnectivityProbe _probe;
    private readonly IAppLog _log;

    public DialogService(
        IPresetTester tester,
        IConfigService config,
        StoragePathService paths,
        IPlatformCapabilities capabilities,
        IPrivilegeService privileges,
        IStartupService startup,
        IConnectivityProbe probe,
        IAppLog log)
    {
        _tester = tester;
        _config = config;
        _paths = paths;
        _capabilities = capabilities;
        _privileges = privileges;
        _startup = startup;
        _probe = probe;
        _log = log;
    }

    public Window? Owner { get; set; }

    public async Task<Preset?> EditPresetAsync(
        AdapterInfo adapter,
        Preset? existing,
        IReadOnlyList<string> wifiProfiles,
        bool isNew = false)
    {
        var viewModel = new PresetEditorViewModel(adapter, existing, wifiProfiles, isNew);
        var window = new PresetEditorWindow(viewModel, _tester, _log);
        return await window.ShowDialog<Preset?>(RequireOwner());
    }

    public async Task ShowTestAsync(
        AdapterInfo adapter,
        string presetName,
        DesiredState desired)
    {
        var viewModel = new TestWindowViewModel(
            adapter,
            presetName,
            desired,
            _tester,
            _config.Config.Settings.Test,
            _log);
        var window = new TestWindow(viewModel);
        await window.ShowDialog(RequireOwner());
    }

    public async Task<bool> ConfirmApplyAsync(
        AdapterInfo adapter,
        Preset preset,
        AdapterState? state,
        bool isOnlyConnectedAdapter = false)
    {
        var window = new ApplyConfirmationWindow(
            adapter, preset, state, _capabilities.IsSimulated, isOnlyConnectedAdapter);
        return await window.ShowDialog<bool>(RequireOwner());
    }

    public async Task<bool> ShowAutoRevertAsync(
        string presetName,
        int seconds,
        string? gatewayAddress = null)
    {
        var window = new AutoRevertWindow(presetName, seconds, gatewayAddress, _probe);
        return await window.ShowDialog<bool>(RequireOwner());
    }

    public async Task<bool> ShowSettingsAsync()
    {
        var viewModel = new SettingsViewModel(
            _paths, _config, _privileges, _capabilities, _startup);
        var window = new SettingsWindow(viewModel);
        return await window.ShowDialog<bool>(RequireOwner());
    }

    public async Task ShowMessageAsync(
        string title,
        string message,
        MessageKind kind = MessageKind.Info)
    {
        var window = new MessageWindow(title, message, kind);
        await window.ShowDialog<bool>(RequireOwner());
    }

    public async Task<string?> PickExportFileAsync(string suggestedName)
    {
        var owner = RequireOwner();
        IStorageFolder? start = null;
        try
        {
            if (Directory.Exists(_paths.Paths.ExportDirectory))
            {
                start = await owner.StorageProvider.TryGetFolderFromPathAsync(
                    _paths.Paths.ExportDirectory);
            }
        }
        catch
        {
            // Picker still opens without a suggested start location.
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出配置",
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            SuggestedStartLocation = start,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON 配置文件") { Patterns = new[] { "*.json" } }
            }
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickImportFileAsync()
    {
        var files = await RequireOwner().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入配置",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON 配置文件") { Patterns = new[] { "*.json" } }
            }
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText,
        bool dangerous = false)
    {
        var window = new MessageWindow(
            title,
            message,
            dangerous ? MessageKind.Warning : MessageKind.Info,
            confirmText,
            cancelText: "取消",
            dangerous: dangerous);

        return await window.ShowDialog<bool>(RequireOwner());
    }

    private Window RequireOwner() =>
        Owner ?? throw new InvalidOperationException("主窗口尚未准备好，无法打开对话框。");
}

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;
using NetSwitch.Core.Storage;

namespace NetSwitch.App.ViewModels;

public sealed record PathPreviewRow(string Label, string Path, string Source);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly StoragePathService _pathService;
    private readonly IConfigService _config;
    private readonly IPrivilegeService _privileges;
    private readonly IPlatformCapabilities _capabilities;
    private readonly IStartupService _startup;
    private bool _loading;
    private bool _dataChanged;
    private bool _originalStartupEnabled;

    public SettingsViewModel(
        StoragePathService pathService,
        IConfigService config,
        IPrivilegeService privileges,
        IPlatformCapabilities capabilities,
        IStartupService startup)
    {
        _pathService = pathService;
        _config = config;
        _privileges = privileges;
        _capabilities = capabilities;
        _startup = startup;

        LoadFrom(pathService.Options);
        LoadSettings(config.Config.Settings);
    }

    public bool StartupSupported => _startup.IsSupported;

    public string BootstrapFile => _pathService.BootstrapFile;

    public string ConfigBackupFile => _pathService.Paths.ConfigFile + ".bak";

    public string DefaultDataRoot => _pathService.DefaultDataRoot;

    public ObservableCollection<PathPreviewRow> PreviewRows { get; } = new();

    // ---- 常规 ----

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _showVirtualAdapters;

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private bool _startWithWindows;

    public async Task LoadStartupStateAsync()
    {
        if (!_startup.IsSupported)
        {
            return;
        }

        try
        {
            _originalStartupEnabled = await _startup.IsEnabledAsync();
            StartWithWindows = _originalStartupEnabled;
        }
        catch (Exception ex)
        {
            DataStatusText = $"读取开机自启状态失败：{ex.Message}";
        }
    }

    // ---- 安全 ----

    [ObservableProperty]
    private bool _confirmBeforeApply;

    [ObservableProperty]
    private int _autoRevertModeIndex;

    [ObservableProperty]
    private int _autoRevertSeconds;

    // ---- 测试 ----

    [ObservableProperty]
    private bool _testPingGateway;

    [ObservableProperty]
    private bool _testDnsServersDirectly;

    [ObservableProperty]
    private bool _testPublicTcp;

    [ObservableProperty]
    private string _testDnsDomain = string.Empty;

    [ObservableProperty]
    private int _testTcpPort;

    [ObservableProperty]
    private int _testTimeoutSeconds;

    [ObservableProperty]
    private int _testTrialMaxSeconds;

    // ---- 存储 ----

    [ObservableProperty]
    private int _storageModeIndex;

    [ObservableProperty]
    private string _dataRoot = string.Empty;

    [ObservableProperty]
    private string _configFile = string.Empty;

    [ObservableProperty]
    private string _snapshotDirectory = string.Empty;

    [ObservableProperty]
    private string _logDirectory = string.Empty;

    [ObservableProperty]
    private string _exportDirectory = string.Empty;

    [ObservableProperty]
    private string _backupDirectory = string.Empty;

    [ObservableProperty]
    private bool _migrateExisting;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string _statusText = "修改后会在保存时校验路径是否可写。";

    [ObservableProperty]
    private bool _hasError;

    // ---- 数据 / 关于 ----

    [ObservableProperty]
    private string _dataStatusText = "导入会合并预设，不会覆盖同名预设。";

    [ObservableProperty]
    private string _resolvedLogDirectory = string.Empty;

    [ObservableProperty]
    private string _resolvedSnapshotDirectory = string.Empty;

    [ObservableProperty]
    private string _resolvedExportDirectory = string.Empty;

    public string AppVersionText { get; } =
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public string PlatformText => _capabilities.IsSimulated
        ? $"{_capabilities.PlatformName}（演示后端）"
        : _capabilities.PlatformName;

    public string ElevationText => _privileges.IsElevated ? "已提权（管理员）" : "普通用户";

    public string DotNetText => Environment.Version.ToString();

    public bool IsCustomMode => StorageModeIndex == 1;

    public event Action<bool>? CloseRequested;

    public event Func<Task<string?>>? BrowseDataRootRequested;

    public event Func<Task<string?>>? BrowseConfigFileRequested;

    public event Func<Task<string?>>? BrowseDirectoryRequested;

    public event Func<string, string?, Task<string?>>? PickSaveFileRequested;

    public event Func<string, Task<string?>>? PickOpenFileRequested;

    public event Func<string, Task<string?>>? PickSaveLogFileRequested;

    public event Action? ViewLogsRequested;

    public event Action<string>? CopyTextRequested;

    /// <summary>Arguments: title, message, confirm text, dangerous. Returns true to proceed.</summary>
    public event Func<string, string, string, bool, Task<bool>>? ConfirmRequested;

    partial void OnStorageModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomMode));
        UpdatePreview();
    }

    partial void OnDataRootChanged(string value) => UpdatePreview();

    partial void OnConfigFileChanged(string value) => UpdatePreview();

    partial void OnSnapshotDirectoryChanged(string value) => UpdatePreview();

    partial void OnLogDirectoryChanged(string value) => UpdatePreview();

    partial void OnExportDirectoryChanged(string value) => UpdatePreview();

    partial void OnBackupDirectoryChanged(string value) => UpdatePreview();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving)
        {
            return;
        }

        if (!ValidateSettings())
        {
            return;
        }

        IsSaving = true;
        HasError = false;
        StatusText = "正在校验并保存设置…";

        var previousStartup = _originalStartupEnabled;
        var startupChanged = false;
        var settingsSaved = false;

        try
        {
            if (_startup.IsSupported && StartWithWindows != previousStartup)
            {
                var startup = await _startup.SetEnabledAsync(StartWithWindows);
                if (!startup.Success)
                {
                    HasError = true;
                    StatusText = startup.Message;
                    return;
                }

                startupChanged = true;
            }

            var result = await _pathService.ChangePathsAsync(BuildOptions(), MigrateExisting);
            if (!result.Success)
            {
                await TryRevertStartupAsync(startupChanged, previousStartup);
                HasError = true;
                StatusText = result.Message;
                return;
            }

            ApplySettingsToConfig();
            await _config.SaveAsync();
            settingsSaved = true;
            _originalStartupEnabled = StartWithWindows;
            ApplyTheme();

            StatusText = result.Message;
            CloseRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            if (startupChanged && !settingsSaved)
            {
                await TryRevertStartupAsync(startupChanged, previousStartup);
            }

            HasError = true;
            StatusText = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void SetUnifiedMode() => StorageModeIndex = 0;

    [RelayCommand]
    private void SetCustomMode() => StorageModeIndex = 1;

    [RelayCommand]
    private void RestoreDefault()
    {
        _loading = true;
        try
        {
            StorageModeIndex = 0;
            DataRoot = DefaultDataRoot;
            ConfigFile = string.Empty;
            SnapshotDirectory = string.Empty;
            LogDirectory = string.Empty;
            ExportDirectory = string.Empty;
            BackupDirectory = string.Empty;
        }
        finally
        {
            _loading = false;
        }

        UpdatePreview();
        StatusText = "已恢复默认位置；点击保存后生效。";
    }

    [RelayCommand]
    private async Task BrowseDataRootAsync()
    {
        if (BrowseDataRootRequested is null)
        {
            return;
        }

        var path = await BrowseDataRootRequested();
        if (!string.IsNullOrWhiteSpace(path))
        {
            DataRoot = path;
        }
    }

    [RelayCommand]
    private async Task BrowseConfigFileAsync()
    {
        if (BrowseConfigFileRequested is null)
        {
            return;
        }

        var path = await BrowseConfigFileRequested();
        if (!string.IsNullOrWhiteSpace(path))
        {
            ConfigFile = path;
        }
    }

    [RelayCommand]
    private async Task BrowseDirectoryAsync(string? which)
    {
        if (BrowseDirectoryRequested is null)
        {
            return;
        }

        var path = await BrowseDirectoryRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        switch (which)
        {
            case "snapshot":
                SnapshotDirectory = path;
                break;
            case "log":
                LogDirectory = path;
                break;
            case "export":
                ExportDirectory = path;
                break;
            case "backup":
                BackupDirectory = path;
                break;
        }
    }

    [RelayCommand]
    private void ViewLogs() => ViewLogsRequested?.Invoke();

    [RelayCommand]
    private void CopyLogPath() => CopyTextRequested?.Invoke(_pathService.Paths.LogDirectory);

    [RelayCommand]
    private async Task ExportLogAsync()
    {
        if (PickSaveLogFileRequested is null)
        {
            return;
        }

        try
        {
            var logDirectory = _pathService.Paths.LogDirectory;
            if (!Directory.Exists(logDirectory))
            {
                DataStatusText = "日志目录不存在。";
                return;
            }

            var latest = new DirectoryInfo(logDirectory)
                .GetFiles("netswitch-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null)
            {
                DataStatusText = "尚无日志可导出。";
                return;
            }

            var path = await PickSaveLogFileRequested(latest.Name);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            File.Copy(latest.FullName, path, overwrite: true);
            DataStatusText = $"已导出日志到 {path}";
        }
        catch (Exception ex)
        {
            DataStatusText = $"导出日志失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        if (PickSaveFileRequested is null)
        {
            return;
        }

        try
        {
            var suggested = $"netswitch-presets-{DateTime.Now:yyyyMMdd-HHmm}.json";
            var path = await PickSaveFileRequested("导出配置", suggested);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await ConfigTransfer.ExportAsync(_config.Config, path);

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    await _pathService.RememberExportDirectoryAsync(directory);
                }
            }
            catch
            {
                // Remembering the folder is optional; the export already succeeded.
            }

            DataStatusText = $"已导出到 {path}";
        }
        catch (Exception ex)
        {
            DataStatusText = $"导出失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        if (PickOpenFileRequested is null)
        {
            return;
        }

        try
        {
            var path = await PickOpenFileRequested("导入配置");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var imported = await ConfigTransfer.ReadAsync(path);
            var count = ConfigTransfer.Merge(_config.Config, imported);
            if (count == 0)
            {
                DataStatusText = "导入文件中没有可合并的预设。";
                return;
            }

            await _config.SaveAsync();
            _dataChanged = true;
            DataStatusText = $"已导入 {count} 个预设；同名预设已自动重命名。";
        }
        catch (Exception ex)
        {
            DataStatusText = $"导入失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CleanLogsAsync()
    {
        try
        {
            var directory = _pathService.Paths.LogDirectory;
            if (!Directory.Exists(directory))
            {
                DataStatusText = "日志目录不存在。";
                return;
            }

            var files = Directory.GetFiles(directory, "netswitch-*.log");
            if (files.Length == 0)
            {
                DataStatusText = "没有可清理的日志文件。";
                return;
            }

            var total = files.Sum(f => new FileInfo(f).Length);
            var confirmed = await ConfirmAsync(
                "清理日志",
                $"将删除 {files.Length} 个日志文件，共 {FormatSize(total)}。\n\n目录：{directory}\n\n" +
                "删除不可撤销。应用继续运行时会重新创建当天日志。",
                "删除日志");
            if (!confirmed)
            {
                return;
            }

            var deleted = 0;
            var failed = 0;
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    failed++;
                }
            }

            DataStatusText = failed == 0
                ? $"已删除 {deleted} 个日志文件。"
                : $"已删除 {deleted} 个日志文件，{failed} 个删除失败（可能正被占用）。";
        }
        catch (Exception ex)
        {
            DataStatusText = $"清理日志失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CleanHistoryAsync()
    {
        try
        {
            var history = Path.Combine(_pathService.Paths.SnapshotDirectory, "history");
            if (!Directory.Exists(history))
            {
                DataStatusText = "没有历史快照。";
                return;
            }

            var files = Directory.GetFiles(history, "*.json");
            if (files.Length == 0)
            {
                DataStatusText = "没有可清理的历史快照。";
                return;
            }

            var confirmed = await ConfirmAsync(
                "清理历史快照",
                $"将删除 {files.Length} 个历史快照。\n\n目录：{history}\n\n" +
                "last.json 与未完成事务的 pending.json 会保留，因此不影响“恢复上次配置”和崩溃恢复。",
                "清理历史");
            if (!confirmed)
            {
                return;
            }

            var deleted = 0;
            var failed = 0;
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    failed++;
                }
            }

            DataStatusText = failed == 0
                ? $"已删除 {deleted} 个历史快照（last/pending 保留）。"
                : $"已删除 {deleted} 个历史快照，{failed} 个删除失败。";
        }
        catch (Exception ex)
        {
            DataStatusText = $"清理失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CleanBackupsAsync()
    {
        try
        {
            var backup = _pathService.Paths.ConfigFile + ".bak";
            if (!File.Exists(backup))
            {
                DataStatusText = "没有配置备份文件。";
                return;
            }

            var size = new FileInfo(backup).Length;
            var confirmed = await ConfirmAsync(
                "清理配置备份",
                $"将删除配置文件的自动备份（{FormatSize(size)}）。\n\n路径：{backup}\n\n" +
                "删除后如果配置文件损坏，应用将无法自动回退到备份。",
                "删除备份");
            if (!confirmed)
            {
                return;
            }

            File.Delete(backup);
            DataStatusText = $"已删除配置备份（{FormatSize(size)}）。";
        }
        catch (Exception ex)
        {
            DataStatusText = $"清理配置备份失败：{ex.Message}";
        }
    }

    private Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var handler = ConfirmRequested;
        return handler is null
            ? Task.FromResult(false)
            : handler(title, message, confirmText, true);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(_dataChanged);

    /// <summary>
    /// Startup registration is applied before the paths are validated because changing paths
    /// can migrate files. If a later step fails, restore the previous startup state so a failed
    /// save does not leave a half-applied setting behind.
    /// </summary>
    private async Task TryRevertStartupAsync(bool startupChanged, bool previousStartup)
    {
        if (!startupChanged)
        {
            return;
        }

        try
        {
            var revert = await _startup.SetEnabledAsync(previousStartup);
            if (revert.Success)
            {
                StartWithWindows = previousStartup;
            }
            else
            {
                DataStatusText = $"开机自启回退失败：{revert.Message}";
            }
        }
        catch (Exception ex)
        {
            DataStatusText = $"开机自启回退失败：{ex.Message}";
        }
    }

    private bool ValidateSettings()
    {
        if (AutoRevertSeconds is < 10 or > 600)
        {
            HasError = true;
            StatusText = "自动恢复倒计时必须在 10 到 600 秒之间。";
            return false;
        }

        if (TestTcpPort is < 1 or > 65535)
        {
            HasError = true;
            StatusText = "测试端口必须在 1 到 65535 之间。";
            return false;
        }

        if (TestTimeoutSeconds is < 1 or > 60)
        {
            HasError = true;
            StatusText = "单项超时必须在 1 到 60 秒之间。";
            return false;
        }

        if (TestTrialMaxSeconds is < 5 or > 180)
        {
            HasError = true;
            StatusText = "试运行最长时长必须在 5 到 180 秒之间。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(TestDnsDomain))
        {
            HasError = true;
            StatusText = "测试域名不能为空。";
            return false;
        }

        return true;
    }

    private void LoadSettings(AppSettings settings)
    {
        CloseToTray = settings.CloseToTray;
        StartMinimized = settings.StartMinimized;
        ShowVirtualAdapters = settings.ShowVirtualAdapters;
        ThemeIndex = string.Equals(settings.Theme, "System", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ConfirmBeforeApply = settings.ConfirmBeforeApply;
        AutoRevertModeIndex = settings.AutoRevertMode switch
        {
            AutoRevertMode.Off => 0,
            AutoRevertMode.Always => 2,
            _ => 1
        };
        AutoRevertSeconds = settings.AutoRevertSeconds;

        var test = settings.Test ?? TestOptions.Default;
        TestPingGateway = test.PingGateway;
        TestDnsServersDirectly = test.TestDnsServersDirectly;
        TestPublicTcp = test.TestPublicTcp;
        TestDnsDomain = test.DnsDomain;
        TestTcpPort = test.TcpPort;
        TestTimeoutSeconds = test.TimeoutSeconds;
        TestTrialMaxSeconds = test.TrialMaxSeconds;
    }

    private void ApplySettingsToConfig()
    {
        var settings = _config.Config.Settings;
        settings.CloseToTray = CloseToTray;
        settings.StartMinimized = StartMinimized;
        settings.ShowVirtualAdapters = ShowVirtualAdapters;
        settings.Theme = ThemeIndex == 1 ? "System" : "Light";
        settings.ConfirmBeforeApply = ConfirmBeforeApply;
        settings.AutoRevertMode = AutoRevertModeIndex switch
        {
            0 => AutoRevertMode.Off,
            2 => AutoRevertMode.Always,
            _ => AutoRevertMode.DangerousOnly
        };
        settings.AutoRevertSeconds = AutoRevertSeconds;
        settings.Test = new TestOptions(
            TestPingGateway,
            TestDnsServersDirectly,
            TestPublicTcp,
            TestDnsDomain.Trim(),
            TestTcpPort,
            TestTimeoutSeconds,
            TestTrialMaxSeconds);
    }

    private void ApplyTheme()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ThemeIndex == 1 ? ThemeVariant.Default : ThemeVariant.Light;
        }
    }

    private void LoadFrom(StorageOptions options)
    {
        _loading = true;
        try
        {
            StorageModeIndex = options.Mode == StorageMode.Custom ? 1 : 0;
            DataRoot = options.DataRoot;

            var overrides = options.Overrides ?? new StoragePathOverrides();
            var paths = StoragePathService.ResolvePaths(options);
            ConfigFile = overrides.ConfigFile ?? paths.ConfigFile;
            SnapshotDirectory = overrides.SnapshotDirectory ?? paths.SnapshotDirectory;
            LogDirectory = overrides.LogDirectory ?? paths.LogDirectory;
            ExportDirectory = overrides.ExportDirectory ?? paths.ExportDirectory;
            BackupDirectory = overrides.BackupDirectory ?? paths.BackupDirectory;

            ResolvedLogDirectory = paths.LogDirectory;
            ResolvedSnapshotDirectory = paths.SnapshotDirectory;
            ResolvedExportDirectory = paths.ExportDirectory;
        }
        finally
        {
            _loading = false;
        }

        UpdatePreview();
    }

    private StorageOptions BuildOptions()
    {
        var overrides = IsCustomMode
            ? new StoragePathOverrides(
                NullIfEmpty(ConfigFile),
                NullIfEmpty(SnapshotDirectory),
                NullIfEmpty(LogDirectory),
                NullIfEmpty(ExportDirectory),
                NullIfEmpty(BackupDirectory))
            : new StoragePathOverrides();

        return new StorageOptions(
            IsCustomMode ? StorageMode.Custom : StorageMode.Unified,
            DataRoot,
            overrides,
            _pathService.Options.LastExportDirectory);
    }

    private void UpdatePreview()
    {
        if (_loading)
        {
            return;
        }

        PreviewRows.Clear();
        try
        {
            var options = BuildOptions();
            var paths = StoragePathService.ResolvePaths(options);
            PreviewRows.Add(new PathPreviewRow(
                "配置文件",
                paths.ConfigFile,
                IsCustomMode && !string.IsNullOrWhiteSpace(ConfigFile) ? "自定义" : "派生"));
            PreviewRows.Add(new PathPreviewRow(
                "快照目录",
                paths.SnapshotDirectory,
                IsCustomMode && !string.IsNullOrWhiteSpace(SnapshotDirectory) ? "自定义" : "派生"));
            PreviewRows.Add(new PathPreviewRow(
                "日志目录",
                paths.LogDirectory,
                IsCustomMode && !string.IsNullOrWhiteSpace(LogDirectory) ? "自定义" : "派生"));
            PreviewRows.Add(new PathPreviewRow(
                "导出目录",
                paths.ExportDirectory,
                IsCustomMode && !string.IsNullOrWhiteSpace(ExportDirectory) ? "自定义" : "派生"));
            PreviewRows.Add(new PathPreviewRow(
                "备份目录",
                paths.BackupDirectory,
                IsCustomMode && !string.IsNullOrWhiteSpace(BackupDirectory) ? "自定义" : "派生"));
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = ex.Message;
        }
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

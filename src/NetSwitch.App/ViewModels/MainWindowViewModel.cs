using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetSwitch.App.Services;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;
using NetSwitch.Core.Storage;
using NetSwitch.Core.Validation;

namespace NetSwitch.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IAdapterCatalog _catalog;
    private readonly INetworkConfigurator _configurator;
    private readonly IWirelessController _wireless;
    private readonly IConfigService _config;
    private readonly IPathService _paths;
    private readonly IPlatformCapabilities _capabilities;
    private readonly IPrivilegeService _privileges;
    private readonly IDialogService _dialogs;
    private readonly IAppLog _log;

    private bool _initialized;
    private bool _suppressSelectionReload;

    public MainWindowViewModel(
        IAdapterCatalog catalog,
        INetworkConfigurator configurator,
        IWirelessController wireless,
        IConfigService config,
        IPathService paths,
        IPlatformCapabilities capabilities,
        IPrivilegeService privileges,
        IDialogService dialogs,
        IAppLog log)
    {
        _catalog = catalog;
        _configurator = configurator;
        _wireless = wireless;
        _config = config;
        _paths = paths;
        _capabilities = capabilities;
        _privileges = privileges;
        _dialogs = dialogs;
        _log = log;
    }

    public ObservableCollection<AdapterItemViewModel> Adapters { get; } = new();

    public ObservableCollection<PresetItemViewModel> Presets { get; } = new();

    [ObservableProperty]
    private AdapterItemViewModel? _selectedAdapter;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "正在加载…";

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private string _statusMessage = "正在加载网络适配器…";

    [ObservableProperty]
    private string _storagePathText = string.Empty;

    public bool CloseToTray => _config.Config.Settings.CloseToTray;

    public bool StartMinimized => _config.Config.Settings.StartMinimized;

    [ObservableProperty]
    private string _backendText = "模拟后端 · M1";

    [ObservableProperty]
    private bool _isElevated;

    [ObservableProperty]
    private string _lastAppliedText = "尚未应用配置";

    [ObservableProperty]
    private string _selectedTitle = "网络适配器";

    [ObservableProperty]
    private string _selectedSubtitle = string.Empty;

    [ObservableProperty]
    private string _selectedStatusText = "未选择";

    [ObservableProperty]
    private bool _selectedIsUp;

    [ObservableProperty]
    private bool _selectedIsWifi;

    [ObservableProperty]
    private string _currentIpv4Text = "—";

    [ObservableProperty]
    private string _currentGatewayText = "—";

    [ObservableProperty]
    private string _currentDnsText = "—";

    [ObservableProperty]
    private string _currentIpv6Text = "—";

    [ObservableProperty]
    private string _currentMetricText = "—";

    [ObservableProperty]
    private string _currentWifiText = "—";

    [ObservableProperty]
    private string _currentSummaryText = "没有可用的网络适配器";

    [ObservableProperty]
    private string _presetCountText = "0 个预设";

    [ObservableProperty]
    private bool _hasPresets;

    [ObservableProperty]
    private bool _showVirtualAdapters;

    [ObservableProperty]
    private bool _hasPendingRecovery;

    [ObservableProperty]
    private bool _hasLastRecovery;

    [ObservableProperty]
    private string _pendingRecoveryText = string.Empty;

    [ObservableProperty]
    private string _lastRecoveryText = string.Empty;

    partial void OnSelectedAdapterChanged(AdapterItemViewModel? value)
    {
        if (_suppressSelectionReload)
        {
            return;
        }

        _ = LoadSelectedAdapterAsync(value);
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        IsBusy = true;
        BusyText = "正在准备配置存储…";

        try
        {
            var init = await _paths.InitializeAsync();
            if (!init.Success)
            {
                await _dialogs.ShowMessageAsync(
                    "存储路径不可用",
                    init.Error ?? "无法准备配置目录。",
                    MessageKind.Error);
            }

            StoragePathText = _paths.Options.DataRoot;
            IsElevated = _privileges.IsElevated;
            BackendText = _capabilities.IsSimulated
                ? $"{_capabilities.PlatformName} · 演示数据"
                : $"{_capabilities.PlatformName} · 真实网卡";

            await _config.LoadAsync();
            ShowVirtualAdapters = _config.Config.Settings.ShowVirtualAdapters;

            var adapters = await _catalog.GetAdaptersAsync();
            if (_capabilities.IsSimulated)
            {
                // Demo presets would otherwise point at made-up addresses on a real machine.
                await SeedDemoPresetsAsync(adapters);
            }

            await RefreshAdaptersAsync();
            await RefreshRecoveryAsync();

            StatusMessage = $"已就绪 · {Adapters.Count} 个适配器 · 配置保存在 {StoragePathText}";
            if (!_capabilities.IsSimulated && !IsElevated)
            {
                StatusMessage = "只读模式：未以管理员身份运行，应用配置需要管理员权限。";
            }

            if (HasPendingRecovery)
            {
                await OfferPendingRecoveryAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "初始化失败";
            await _dialogs.ShowMessageAsync("初始化失败", ex.Message, MessageKind.Error);
        }
        finally
        {
            IsBusy = false;
            IsReady = true;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyText = "正在刷新网络状态…";
        try
        {
            await RefreshAdaptersAsync();
            StatusMessage = $"已刷新 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("刷新失败", ex.Message, MessageKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task NewPresetAsync()
    {
        if (SelectedAdapter is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var template = new Preset(
            Guid.NewGuid().ToString("N"),
            "新预设",
            string.Empty,
            DesiredState.Dhcp(),
            now,
            now);
        await EditPresetCoreAsync(template, isNew: true);
    }

    [RelayCommand]
    private async Task CreateFromCurrentAsync()
    {
        var adapter = SelectedAdapter;
        if (adapter is null)
        {
            return;
        }

        try
        {
            // Re-read the adapter first: the cached state can be several seconds stale and the
            // whole point is to capture what is actually configured right now.
            var state = await LoadSelectedAdapterAsync(adapter);
            if (state is null)
            {
                await _dialogs.ShowMessageAsync(
                    "无法读取当前状态",
                    "无法读取该适配器的网络状态，暂时不能据此创建预设。请先刷新适配器后重试。",
                    MessageKind.Warning);
                return;
            }

            var now = DateTimeOffset.Now;
            var preset = PresetFactory.FromState($"当前配置 {now:MM-dd HH:mm}", state, now);
            await EditPresetCoreAsync(preset, isNew: true);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("无法创建预设", ex.Message, MessageKind.Error);
        }
    }

    /// <summary>Copies an existing preset into a new one and opens the editor on the copy.</summary>
    [RelayCommand]
    private async Task CreateFromPresetAsync(PresetItemViewModel? item)
    {
        if (item is null || SelectedAdapter is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var adapterConfig = _config.Config.Adapters.FirstOrDefault(a =>
            AppConfig.AdapterKeysEqual(a.Key, SelectedAdapter.Key));
        var name = BuildCopyName(item.Name, adapterConfig?.Presets ?? (IReadOnlyList<Preset>)Array.Empty<Preset>());
        var preset = PresetFactory.CloneAsNew(item.Preset, name, now);
        await EditPresetCoreAsync(preset, isNew: true);
    }

    [RelayCommand]
    private async Task EditPresetAsync(PresetItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await EditPresetCoreAsync(item.Preset);
    }

    [RelayCommand]
    private async Task TestPresetAsync(PresetItemViewModel? item)
    {
        if (item is null || SelectedAdapter is null)
        {
            return;
        }

        try
        {
            await _dialogs.ShowTestAsync(SelectedAdapter.Info, item.Name, item.Preset.Desired);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("测试失败", ex.Message, MessageKind.Error);
        }
    }

    [RelayCommand]
    private Task ApplyPresetAsync(PresetItemViewModel? item) =>
        item is null || SelectedAdapter is null
            ? Task.CompletedTask
            : ApplyPresetCoreAsync(
                SelectedAdapter,
                item.Preset,
                item.Id,
                item.Name,
                askForConfirmation: true,
                isTrayInvocation: false);

    /// <summary>
    /// Applies a preset from the tray menu. The design intentionally skips the confirmation
    /// dialog for tray invocation; dangerous changes still get the auto-revert countdown.
    /// </summary>
    public async Task ApplyPresetFromTrayAsync(AdapterItemViewModel adapter, Preset preset)
    {
        try
        {
            // The tray menu may have been built before the latest state refresh.
            var state = await _catalog.GetStateAsync(adapter.Key);
            if (state is not null)
            {
                adapter.Update(adapter.Info, state);
            }
        }
        catch
        {
            // Keep the last known state; the configurator snapshots again before writing.
        }

        await ApplyPresetCoreAsync(
            adapter,
            preset,
            preset.Id,
            preset.Name,
            askForConfirmation: false,
            isTrayInvocation: true);
    }

    public IReadOnlyList<Preset> GetPresetsFor(AdapterKey key) =>
        _config.Config.Adapters
            .FirstOrDefault(a => AppConfig.AdapterKeysEqual(a.Key, key))
            ?.Presets
            .ToArray() ?? Array.Empty<Preset>();

    public AdapterItemViewModel? FindAdapter(AdapterKey key) =>
        Adapters.FirstOrDefault(a =>
            string.Equals(a.Key.StableId, key.StableId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Raised for tray-triggered apply results; consumed by the tray notifications.</summary>
    public event Action<string, bool>? NoticeRequested;

    private async Task ApplyPresetCoreAsync(
        AdapterItemViewModel adapter,
        Preset preset,
        string presetId,
        string presetName,
        bool askForConfirmation,
        bool isTrayInvocation)
    {
        if (IsBusy)
        {
            return;
        }

        var settings = _config.Config.Settings;
        var currentState = adapter.State;
        var isOnlyConnected = IsOnlyConnectedAdapter(adapter);
        var risk = ChangeRisk.Evaluate(preset.Desired, currentState, isOnlyConnected);

        if (askForConfirmation && settings.ConfirmBeforeApply)
        {
            var confirmed = await _dialogs.ConfirmApplyAsync(
                adapter.Info, preset, currentState, isOnlyConnected);
            if (!confirmed)
            {
                return;
            }
        }

        IsBusy = true;
        BusyText = $"正在应用「{presetName}」…";
        await LogAsync($"应用开始：适配器「{adapter.Name}」，预设「{presetName}」");

        try
        {
            var options = new ApplyOptions(
                settings.AutoRevertMode,
                settings.AutoRevertSeconds,
                settings.ConfirmBeforeApply,
                Guid.NewGuid().ToString("N"),
                TransactionKind.Apply);

            var result = await _configurator.ApplyAsync(adapter.Key, preset.Desired, options);
            if (!result.Success)
            {
                await LogAsync($"应用失败：适配器「{adapter.Name}」，预设「{presetName}」：{result.Message}");
                StatusMessage = "应用失败：配置未生效";
                if (isTrayInvocation)
                {
                    NoticeRequested?.Invoke($"应用「{presetName}」失败：{result.Message}", true);
                }
                else
                {
                    await _dialogs.ShowMessageAsync("应用失败", result.Message, MessageKind.Error);
                }

                await RefreshRecoveryAsync();
                return;
            }

            var adapterConfig = _config.GetOrCreateAdapter(adapter.Key);
            adapterConfig.LastAppliedPresetId = presetId;
            adapterConfig.LastAppliedAt = DateTimeOffset.Now;
            await _config.SaveAsync();

            await RefreshAdaptersAsync();
            await RefreshRecoveryAsync();
            await LogAsync($"应用成功：适配器「{adapter.Name}」，预设「{presetName}」：{result.Message}");
            StatusMessage = result.Message;
            if (isTrayInvocation)
            {
                NoticeRequested?.Invoke($"已切换到「{presetName}」", false);
            }

            var autoRevert = settings.AutoRevertMode == AutoRevertMode.Always ||
                             (settings.AutoRevertMode == AutoRevertMode.DangerousOnly && risk.IsDangerous);
            if (autoRevert)
            {
                BusyText = $"已应用「{presetName}」，等待确认…";
                var gatewayAddress = FindAdapter(adapter.Key)?.State?.Ipv4.Gateways.FirstOrDefault()?.Address
                                     ?? preset.Desired.Ipv4.Gateways.FirstOrDefault()?.Address;
                var shouldRevert = await _dialogs.ShowAutoRevertAsync(
                    presetName, settings.AutoRevertSeconds, gatewayAddress);
                if (shouldRevert)
                {
                    await LogAsync($"倒计时结束：开始自动恢复「{presetName}」应用前的配置");
                    var reverted = await RevertLastCoreAsync("倒计时结束，正在自动恢复…");
                    if (isTrayInvocation)
                    {
                        NoticeRequested?.Invoke(
                            reverted
                                ? $"已自动恢复到应用「{presetName}」之前的配置"
                                : "自动恢复失败，请在主窗口检查网络状态",
                            !reverted);
                    }
                }
                else
                {
                    await LogAsync($"已保持应用「{presetName}」（未回滚）");
                    StatusMessage = $"已保持应用「{presetName}」";
                }
            }
        }
        catch (Exception ex)
        {
            await LogAsync($"应用异常：适配器「{adapter.Name}」，预设「{presetName}」：{ex.Message}");
            StatusMessage = "应用失败";
            if (isTrayInvocation)
            {
                NoticeRequested?.Invoke($"应用「{presetName}」失败：{ex.Message}", true);
            }
            else
            {
                await _dialogs.ShowMessageAsync("应用失败", ex.Message, MessageKind.Error);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RecoverPendingAsync()
    {
        if (IsBusy || !HasPendingRecovery)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "恢复未完成的事务",
            $"{PendingRecoveryText}\n\n" +
            "恢复会把该适配器写回到那次操作开始前的网络参数。" +
            "如果当前正通过该网络远程连接本机，恢复过程可能短暂断网。",
            "恢复",
            dangerous: true);
        if (!confirmed)
        {
            return;
        }

        await RecoverPendingCoreAsync();
    }

    private async Task RecoverPendingCoreAsync()
    {
        IsBusy = true;
        BusyText = "正在恢复未完成的事务…";
        await LogAsync("开始恢复未完成的网络事务");
        try
        {
            var result = await _configurator.RecoverPendingAsync();
            await RefreshRecoveryAsync();
            if (!result.Success)
            {
                await LogAsync($"恢复未完成事务失败：{result.Message}");
                await _dialogs.ShowMessageAsync("恢复失败", result.Message, MessageKind.Error);
                StatusMessage = "恢复未完成事务失败";
                return;
            }

            await RefreshAdaptersAsync();
            await LogAsync($"恢复未完成事务成功：{result.Message}");
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            await LogAsync($"恢复未完成事务异常：{ex.Message}");
            await _dialogs.ShowMessageAsync("恢复失败", ex.Message, MessageKind.Error);
            StatusMessage = "恢复未完成事务失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreLastAsync()
    {
        if (IsBusy || !HasLastRecovery)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "恢复上次配置",
            $"{LastRecoveryText}\n\n" +
            "此操作会把适配器恢复成最近一次成功应用预设之前的网络参数。" +
            "如果当前正通过该网络远程连接本机，可能会短暂断网。",
            "恢复",
            dangerous: true);
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        BusyText = "正在恢复上次配置…";
        _ = await RevertLastCoreAsync("正在恢复上次配置…");
    }

    /// <summary>
    /// Restores the snapshot taken before the last successful apply. Shared by the manual
    /// command and the auto-revert countdown.
    /// </summary>
    private async Task<bool> RevertLastCoreAsync(string busyText)
    {
        IsBusy = true;
        BusyText = busyText;
        try
        {
            var result = await _configurator.RestoreLastAsync();
            await RefreshRecoveryAsync();
            if (!result.Success)
            {
                await LogAsync($"恢复上次配置失败：{result.Message}");
                await _dialogs.ShowMessageAsync("恢复失败", result.Message, MessageKind.Error);
                StatusMessage = "恢复上次配置失败";
                return false;
            }

            await RefreshAdaptersAsync();
            await LogAsync($"恢复上次配置成功：{result.Message}");
            StatusMessage = result.Message;
            return true;
        }
        catch (Exception ex)
        {
            await LogAsync($"恢复上次配置异常：{ex.Message}");
            await _dialogs.ShowMessageAsync("恢复失败", ex.Message, MessageKind.Error);
            StatusMessage = "恢复上次配置失败";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsOnlyConnectedAdapter(AdapterItemViewModel adapter) =>
        adapter.IsUp && Adapters.Count(a => a.IsUp) == 1;

    private async Task RefreshRecoveryAsync()
    {
        try
        {
            var info = await _configurator.GetRecoveryInfoAsync();
            HasPendingRecovery = info.HasPending;
            HasLastRecovery = !info.HasPending && info.HasLast;

            PendingRecoveryText = info.HasPending
                ? $"检测到未完成的事务：{info.PendingAdapterName ?? "未知适配器"}" +
                  (info.PendingStartedAt is { } started
                      ? $"（{started:yyyy-MM-dd HH:mm} 开始）"
                      : string.Empty)
                : string.Empty;

            LastRecoveryText = info.HasLast
                ? $"可恢复「{info.LastAdapterName ?? "上次适配器"}」在上次成功应用" +
                  (info.LastAppliedAt is { } applied ? $"（{applied:yyyy-MM-dd HH:mm}）" : string.Empty) +
                  "之前的网络参数。"
                : string.Empty;
        }
        catch (Exception ex)
        {
            HasPendingRecovery = false;
            HasLastRecovery = false;
            StatusMessage = $"读取恢复信息失败：{ex.Message}";
        }
    }

    private async Task OfferPendingRecoveryAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "检测到未完成的事务",
            $"{PendingRecoveryText}\n\n" +
            "上次操作可能在写入网络配置时被中断（例如程序崩溃或断电）。" +
            "建议立即恢复，避免适配器停留在中间状态。",
            "立即恢复",
            dangerous: true);

        if (!confirmed)
        {
            StatusMessage = "存在未完成的事务，可稍后点击“恢复未完成事务”。";
            return;
        }

        await RecoverPendingCoreAsync();
    }

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var suggested = $"netswitch-presets-{DateTime.Now:yyyyMMdd-HHmm}.json";
            var path = await _dialogs.PickExportFileAsync(suggested);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await ConfigTransfer.ExportAsync(_config.Config, path);

            try
            {
                if (_paths is StoragePathService storage)
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        await storage.RememberExportDirectoryAsync(directory);
                    }
                }
            }
            catch
            {
                // Remembering the folder is optional; the export already succeeded.
            }

            StatusMessage = $"已导出预设到 {path}";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("导出失败", ex.Message, MessageKind.Error);
        }
    }

    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var path = await _dialogs.PickImportFileAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var imported = await ConfigTransfer.ReadAsync(path);
            var count = ConfigTransfer.Merge(_config.Config, imported);
            if (count == 0)
            {
                await _dialogs.ShowMessageAsync(
                    "没有可导入的预设",
                    "导入文件中没有预设，现有配置未改变。",
                    MessageKind.Info);
                return;
            }

            await _config.SaveAsync();
            await RefreshAdaptersAsync();
            StatusMessage = $"已导入 {count} 个预设（同名预设已重命名）";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("导入失败", ex.Message, MessageKind.Error);
        }
    }

    [RelayCommand]
    private async Task DeletePresetAsync(PresetItemViewModel? item)
    {
        if (item is null || SelectedAdapter is null)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "删除预设",
            $"确定删除预设「{item.Name}」吗？此操作不可撤销，但不会改变当前网络配置。",
            "删除",
            dangerous: true);

        if (!confirmed)
        {
            return;
        }

        await _config.DeletePresetAsync(SelectedAdapter.Key, item.Id);
        await LoadPresetsAsync(SelectedAdapter);
        StatusMessage = $"已删除预设「{item.Name}」";
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var changed = await _dialogs.ShowSettingsAsync();
        if (!changed)
        {
            return;
        }

        IsBusy = true;
        BusyText = "正在重新加载配置…";
        try
        {
            await _config.LoadAsync();
            StoragePathText = _paths.Options.DataRoot;
            ShowVirtualAdapters = _config.Config.Settings.ShowVirtualAdapters;
            OnPropertyChanged(nameof(CloseToTray));
            OnPropertyChanged(nameof(StartMinimized));
            await RefreshAdaptersAsync();
            StatusMessage = "存储设置已更新";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("重新加载失败", ex.Message, MessageKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EditPresetCoreAsync(Preset preset, bool isNew = false)
    {
        if (SelectedAdapter is null)
        {
            return;
        }

        try
        {
            var profiles = SelectedAdapter.IsWifi
                ? await _wireless.GetSavedProfilesAsync(SelectedAdapter.Key)
                : Array.Empty<string>();

            var result = await _dialogs.EditPresetAsync(
                SelectedAdapter.Info, preset, profiles, isNew);
            if (result is null)
            {
                return;
            }

            await _config.AddOrUpdatePresetAsync(SelectedAdapter.Key, result);
            await LoadPresetsAsync(SelectedAdapter);
            StatusMessage = $"已保存预设「{result.Name}」";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("保存失败", ex.Message, MessageKind.Error);
        }
    }

    private async Task RefreshAdaptersAsync()
    {
        var infos = await _catalog.GetAdaptersAsync();
        var states = new Dictionary<string, AdapterState?>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in infos)
        {
            states[info.Key.StableId] = await _catalog.GetStateAsync(info.Key);
        }

        var selectedId = SelectedAdapter?.Key.StableId;
        _suppressSelectionReload = true;
        try
        {
            Adapters.Clear();
            foreach (var info in infos)
            {
                if (info.Kind == AdapterKind.Virtual && !ShowVirtualAdapters)
                {
                    continue;
                }

                Adapters.Add(new AdapterItemViewModel(info, states[info.Key.StableId]));
            }

            SelectedAdapter = Adapters.FirstOrDefault(a =>
                                 string.Equals(a.Key.StableId, selectedId, StringComparison.OrdinalIgnoreCase))
                             ?? Adapters.FirstOrDefault(a => a.IsUp)
                             ?? Adapters.FirstOrDefault();
        }
        finally
        {
            _suppressSelectionReload = false;
        }

        await LoadSelectedAdapterAsync(SelectedAdapter);
    }

    private async Task<AdapterState?> LoadSelectedAdapterAsync(AdapterItemViewModel? adapter)
    {
        if (adapter is null)
        {
            ClearSelection();
            return null;
        }

        AdapterState? state = null;
        try
        {
            state = await _catalog.GetStateAsync(adapter.Key);
            if (state is not null)
            {
                adapter.Update(adapter.Info, state);
            }

            SelectedTitle = adapter.Name;
            SelectedSubtitle = $"{adapter.Description} · {KindText(adapter.Info.Kind)}";
            SelectedStatusText = adapter.StatusText;
            SelectedIsUp = adapter.IsUp;
            SelectedIsWifi = adapter.IsWifi;

            if (state is null)
            {
                CurrentIpv4Text = "—";
                CurrentGatewayText = "—";
                CurrentDnsText = "—";
                CurrentIpv6Text = "—";
                CurrentMetricText = "—";
                CurrentWifiText = "—";
                CurrentSummaryText = "无法读取该适配器的当前状态";
            }
            else
            {
                CurrentIpv4Text = DisplayFormat.Addresses(state.Ipv4.Addresses);
                CurrentGatewayText = DisplayFormat.Gateways(state.Ipv4.Gateways);
                CurrentDnsText = DisplayFormat.Dns(state.Ipv4.DnsServers, state.Ipv4.DnsMode);
                CurrentIpv6Text = DisplayFormat.Ipv6Summary(state.Ipv6);
                CurrentMetricText = DisplayFormat.Metric(
                    state.Ipv4.AutomaticMetric,
                    state.Ipv4.InterfaceMetric);
                CurrentWifiText = state.Wireless is { IsConnected: true }
                    ? state.Wireless.ActiveProfileName ?? state.Wireless.Ssid ?? "已连接"
                    : "未连接";
                CurrentSummaryText =
                    $"当前：{(state.Ipv4.DhcpEnabled ? "DHCP" : "静态")} · " +
                    $"{DisplayFormat.Addresses(state.Ipv4.Addresses)} · " +
                    $"网关 {DisplayFormat.Gateways(state.Ipv4.Gateways)} · " +
                    $"DNS {(state.Ipv4.DnsMode == DnsMode.Auto ? "自动" : "静态")}";
            }

            await LoadPresetsAsync(adapter);
            return state;
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取适配器状态失败：{ex.Message}";
            return null;
        }
    }

    private Task LoadPresetsAsync(AdapterItemViewModel adapter)
    {
        var state = adapter.State;
        var adapterConfig = _config.Config.Adapters.FirstOrDefault(a =>
            AppConfig.AdapterKeysEqual(a.Key, adapter.Key));

        Presets.Clear();
        if (adapterConfig is not null)
        {
            foreach (var preset in adapterConfig.Presets)
            {
                var isCurrent = state is not null && PresetFingerprint.Matches(preset.Desired, state);
                var isLastApplied = string.Equals(
                    adapterConfig.LastAppliedPresetId,
                    preset.Id,
                    StringComparison.Ordinal);

                var item = new PresetItemViewModel(
                    preset,
                    p => _ = TestPresetAsync(p),
                    p => _ = EditPresetAsync(p),
                    p => _ = CreateFromPresetAsync(p),
                    p => _ = ApplyPresetAsync(p),
                    p => _ = DeletePresetAsync(p));

                item.Update(preset, isCurrent, isLastApplied, adapterConfig.LastAppliedAt);
                Presets.Add(item);
            }
        }

        PresetCountText = $"{Presets.Count} 个预设";
        HasPresets = Presets.Count > 0;
        UpdateLastAppliedText(adapterConfig);
        return Task.CompletedTask;
    }

    private void UpdateLastAppliedText(AdapterConfig? adapterConfig)
    {
        if (adapterConfig?.LastAppliedPresetId is null)
        {
            LastAppliedText = "尚未应用配置";
            return;
        }

        var preset = adapterConfig.Presets.FirstOrDefault(p =>
            string.Equals(p.Id, adapterConfig.LastAppliedPresetId, StringComparison.Ordinal));
        LastAppliedText = preset is null
            ? "尚未应用配置"
            : $"上次应用：{preset.Name}（{DisplayFormat.RelativeTime(adapterConfig.LastAppliedAt)}）";
    }

    private void ClearSelection()
    {
        SelectedTitle = "网络适配器";
        SelectedSubtitle = string.Empty;
        SelectedStatusText = "未选择";
        SelectedIsUp = false;
        SelectedIsWifi = false;
        CurrentIpv4Text = "—";
        CurrentGatewayText = "—";
        CurrentDnsText = "—";
        CurrentIpv6Text = "—";
        CurrentMetricText = "—";
        CurrentWifiText = "—";
        CurrentSummaryText = "没有可用的网络适配器";
        Presets.Clear();
        PresetCountText = "0 个预设";
        HasPresets = false;
        LastAppliedText = "尚未应用配置";
    }

    private static string BuildCopyName(string sourceName, IReadOnlyList<Preset> existing)
    {
        var names = existing.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = $"{sourceName}（副本）";
        if (!names.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 2; ; index++)
        {
            var candidate = $"{sourceName}（副本 {index}）";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private async Task LogAsync(string message)
    {
        try
        {
            await _log.WriteAsync(message);
        }
        catch
        {
            // Logging must never break a network operation.
        }
    }

    private async Task SeedDemoPresetsAsync(IReadOnlyList<AdapterInfo> adapters)
    {
        if (_config.Config.Adapters.Any(a => a.Presets.Count > 0))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var ethernet = adapters.FirstOrDefault(a => a.Kind == AdapterKind.Ethernet && a.IsUp)
                       ?? adapters.FirstOrDefault(a => a.IsUp);
        var wifi = adapters.FirstOrDefault(a => a.Kind == AdapterKind.WiFi);

        if (ethernet is not null)
        {
            var adapter = _config.GetOrCreateAdapter(ethernet.Key);

            var dhcp = new Preset(
                "demo-main-dhcp",
                "主路由-DHCP",
                "家庭主路由，动态获取地址。",
                DesiredState.Dhcp(),
                now.AddDays(-30),
                now.AddMinutes(-3));

            var softRouter = new Preset(
                "demo-soft-router",
                "软路由-静态",
                "静态 IP · 主用 192.168.2.100/24",
                new DesiredState(
                    new Ipv4Desired(
                        Ipv4Mode.Static,
                        new[] { new Ipv4Address("192.168.2.100", 24) },
                        new[] { new Gateway("192.168.2.1", 10) },
                        DnsMode.Static,
                        new[] { "192.168.2.1", "223.5.5.5" },
                        MetricMode.Automatic,
                        null),
                    Ipv6Desired.Keep(),
                    WirelessDesired.Keep()),
                now.AddDays(-20),
                now.AddDays(-1));

            var company = new Preset(
                "demo-company",
                "公司-固定IP",
                "静态 IP · 公司内网，禁用 IPv6",
                new DesiredState(
                    new Ipv4Desired(
                        Ipv4Mode.Static,
                        new[] { new Ipv4Address("10.20.8.45", 24) },
                        new[] { new Gateway("10.20.8.1", 5) },
                        DnsMode.Static,
                        new[] { "10.20.8.10", "10.20.8.11" },
                        MetricMode.Automatic,
                        null),
                    new Ipv6Desired(
                        Ipv6Mode.Disabled,
                        Array.Empty<Ipv4Address>(),
                        Array.Empty<Gateway>(),
                        DnsMode.Auto,
                        Array.Empty<string>(),
                        MetricMode.Keep,
                        null),
                    WirelessDesired.Keep()),
                now.AddDays(-60),
                now.AddDays(-7));

            var debugDns = new Preset(
                "demo-debug-dns",
                "调试-DNS",
                "DHCP · 仅覆盖 DNS 服务器",
                new DesiredState(
                    Ipv4Desired.Dhcp() with
                    {
                        DnsMode = DnsMode.Static,
                        DnsServers = new[] { "223.5.5.5", "119.29.29.29" }
                    },
                    Ipv6Desired.Keep(),
                    WirelessDesired.Keep()),
                now.AddDays(-10),
                now.AddDays(-2));

            adapter.Presets.AddRange(new[] { dhcp, softRouter, company, debugDns });
            adapter.LastAppliedPresetId = dhcp.Id;
            adapter.LastAppliedAt = now.AddMinutes(-2);
        }

        if (wifi is not null)
        {
            var adapter = _config.GetOrCreateAdapter(wifi.Key);
            adapter.Presets.Add(new Preset(
                "demo-company-wifi",
                "公司 Wi-Fi",
                "连接已保存的「公司-WiFi」，并动态获取地址。",
                new DesiredState(
                    Ipv4Desired.Dhcp(),
                    Ipv6Desired.Keep(),
                    new WirelessDesired(WirelessAction.Connect, "公司-WiFi")),
                now.AddDays(-15),
                now.AddHours(-3)));
        }

        await _config.SaveAsync();
    }

    private static string KindText(AdapterKind kind) => kind switch
    {
        AdapterKind.WiFi => "Wi-Fi",
        AdapterKind.Ethernet => "以太网",
        AdapterKind.Virtual => "虚拟适配器",
        _ => "其他"
    };
}

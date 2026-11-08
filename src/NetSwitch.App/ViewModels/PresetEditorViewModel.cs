using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;

namespace NetSwitch.App.ViewModels;

public sealed partial class PresetEditorViewModel : ObservableObject
{
    private readonly Preset? _existing;
    private bool _loading;

    public PresetEditorViewModel(
        AdapterInfo adapter,
        Preset? existing,
        IReadOnlyList<string> wifiProfiles,
        bool isNew = false)
    {
        Adapter = adapter;
        _existing = existing;

        Title = isNew
            ? existing is null ? "新建预设" : $"新建预设 · 基于「{existing.Name}」"
            : existing is null ? "新建预设" : $"编辑预设 · {existing.Name}";
        AdapterLine = $"{adapter.Key.FriendlyName} · {adapter.Description}";
        IsWifiAdapter = adapter.Kind == AdapterKind.WiFi;

        foreach (var profile in wifiProfiles)
        {
            WifiProfiles.Add(profile);
        }

        if (existing is null)
        {
            AddAddress();
            AddGateway();
            AddDns();
            return;
        }

        Name = existing.Name;
        Notes = existing.Notes ?? string.Empty;
        LoadDesired(existing.Desired);
    }

    public string Title { get; }

    public string AdapterLine { get; }

    public AdapterInfo Adapter { get; }

    public bool IsWifiAdapter { get; }

    public ObservableCollection<AddressRowViewModel> Addresses { get; } = new();

    public ObservableCollection<GatewayRowViewModel> Gateways { get; } = new();

    public ObservableCollection<DnsRowViewModel> DnsServers { get; } = new();

    public ObservableCollection<AddressRowViewModel> Ipv6Addresses { get; } = new();

    public ObservableCollection<GatewayRowViewModel> Ipv6Gateways { get; } = new();

    public ObservableCollection<DnsRowViewModel> Ipv6DnsServers { get; } = new();

    public ObservableCollection<string> WifiProfiles { get; } = new();

    public string[] MetricModeOptions { get; } = { "保持当前值", "自动", "固定" };

    public string[] Ipv6ModeOptions { get; } = { "保持系统状态", "自动获取", "静态", "禁用 IPv6 绑定" };

    public string[] WirelessActionOptions { get; } = { "保持当前 Wi-Fi", "连接已保存的 Profile", "断开 Wi-Fi" };

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private Ipv4Mode _ipv4Mode = Ipv4Mode.Static;

    [ObservableProperty]
    private bool _isDnsStatic;

    [ObservableProperty]
    private int _metricModeIndex = 1;

    [ObservableProperty]
    private string _interfaceMetric = "25";

    [ObservableProperty]
    private int _ipv6ModeIndex;

    [ObservableProperty]
    private bool _isIpv6DnsStatic;

    [ObservableProperty]
    private int _ipv6MetricModeIndex;

    [ObservableProperty]
    private string _ipv6InterfaceMetric = "25";

    [ObservableProperty]
    private int _wirelessActionIndex;

    [ObservableProperty]
    private string? _selectedProfile;

    [ObservableProperty]
    private string _validationMessage = "字段校验通过";

    [ObservableProperty]
    private bool _hasValidationError;

    [ObservableProperty]
    private bool _isDirty;

    public bool IsIpv4Dhcp
    {
        get => Ipv4Mode == Ipv4Mode.Dhcp;
        set
        {
            if (value)
            {
                Ipv4Mode = Ipv4Mode.Dhcp;
            }
        }
    }

    public bool IsIpv4Static
    {
        get => Ipv4Mode == Ipv4Mode.Static;
        set
        {
            if (value)
            {
                Ipv4Mode = Ipv4Mode.Static;
            }
        }
    }

    public bool IsDnsAuto
    {
        get => !IsDnsStatic;
        set
        {
            if (value)
            {
                IsDnsStatic = false;
            }
        }
    }

    public bool IsIpv6Static => Ipv6ModeIndex == 2;

    public bool IsIpv6Disabled => Ipv6ModeIndex == 3;

    /// <summary>Auto and static both manage IPv6; DNS and metric apply to either mode.</summary>
    public bool IsIpv6Managed => Ipv6ModeIndex is 1 or 2;

    public bool IsMetricManual => MetricModeIndex == 2;

    public bool IsIpv6DnsAuto
    {
        get => !IsIpv6DnsStatic;
        set
        {
            if (value)
            {
                IsIpv6DnsStatic = false;
            }
        }
    }

    public bool IsIpv6MetricManual => Ipv6MetricModeIndex == 2;

    public bool IsWirelessConnect => WirelessActionIndex == 1;

    public bool IsWirelessDisconnect => WirelessActionIndex == 2;

    public bool HasWifiProfiles => WifiProfiles.Count > 0;

    public event Func<DesiredState, Task>? TestRequested;

    public event Action<Preset>? SaveRequested;

    public event Action? CancelRequested;

    partial void OnIpv4ModeChanged(Ipv4Mode value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }

        OnPropertyChanged(nameof(IsIpv4Dhcp));
        OnPropertyChanged(nameof(IsIpv4Static));
        if (!_loading && value == Ipv4Mode.Static && Addresses.Count == 0)
        {
            AddAddress();
        }
    }

    partial void OnIsDnsStaticChanged(bool value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }

        OnPropertyChanged(nameof(IsDnsAuto));
    }

    partial void OnMetricModeIndexChanged(int value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }

        OnPropertyChanged(nameof(IsMetricManual));
    }

    partial void OnIpv6ModeIndexChanged(int value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }

        OnPropertyChanged(nameof(IsIpv6Static));
        OnPropertyChanged(nameof(IsIpv6Disabled));
        OnPropertyChanged(nameof(IsIpv6Managed));
        if (!_loading && value == 2 && Ipv6Addresses.Count == 0)
        {
            AddIpv6Address();
        }
    }

    partial void OnIsIpv6DnsStaticChanged(bool value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }

        OnPropertyChanged(nameof(IsIpv6DnsAuto));
    }

    partial void OnIpv6MetricModeIndexChanged(int value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }

        OnPropertyChanged(nameof(IsIpv6MetricManual));
    }

    partial void OnWirelessActionIndexChanged(int value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }

        OnPropertyChanged(nameof(IsWirelessConnect));
        OnPropertyChanged(nameof(IsWirelessDisconnect));
        if (!_loading && value == 1 && string.IsNullOrEmpty(SelectedProfile) && WifiProfiles.Count > 0)
        {
            SelectedProfile = WifiProfiles[0];
        }
    }

    partial void OnNameChanged(string value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }
    }

    partial void OnNotesChanged(string value)
    {
        if (!_loading)
        {
            IsDirty = true;
        }
    }

    [RelayCommand]
    private void AddAddress()
    {
        IsDirty = true;
        Addresses.Add(new AddressRowViewModel(row => Addresses.Remove(row)));
    }

    [RelayCommand]
    private void AddGateway()
    {
        IsDirty = true;
        Gateways.Add(new GatewayRowViewModel(row => Gateways.Remove(row)));
    }

    [RelayCommand]
    private void AddDns()
    {
        IsDirty = true;
        DnsServers.Add(new DnsRowViewModel(row => DnsServers.Remove(row)));
    }

    [RelayCommand]
    private void AddIpv6Address()
    {
        IsDirty = true;
        Ipv6Addresses.Add(new AddressRowViewModel(row => Ipv6Addresses.Remove(row)) { PrefixLength = "64" });
    }

    [RelayCommand]
    private void AddIpv6Gateway()
    {
        IsDirty = true;
        Ipv6Gateways.Add(new GatewayRowViewModel(row => Ipv6Gateways.Remove(row)));
    }

    [RelayCommand]
    private void AddIpv6Dns()
    {
        IsDirty = true;
        Ipv6DnsServers.Add(new DnsRowViewModel(row => Ipv6DnsServers.Remove(row)));
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        var desired = BuildDesired();
        if (TestRequested is not null)
        {
            await TestRequested(desired);
        }
    }

    [RelayCommand]
    private void Save()
    {
        var checks = DesiredStateValidator.Validate(BuildDesired()).ToList();
        if (string.IsNullOrWhiteSpace(Name))
        {
            checks.Add(TestCheck.Fail("预设名称", "请输入预设名称。"));
        }

        var failures = checks.Where(c => c.Status == TestStatus.Fail).ToList();
        if (failures.Count > 0)
        {
            HasValidationError = true;
            ValidationMessage = string.Join("；", failures.Select(f => $"{f.Name}：{f.Message}"));
            return;
        }

        HasValidationError = false;
        var now = DateTimeOffset.Now;
        var preset = new Preset(
            _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name.Trim(),
            string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            BuildDesired(),
            _existing?.CreatedAt ?? now,
            now);

        SaveRequested?.Invoke(preset);
    }

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke();

    public DesiredState BuildDesired()
    {
        var ipv4 = new Ipv4Desired(
            Ipv4Mode,
            Ipv4Mode == Ipv4Mode.Static ? BuildAddresses(Addresses) : Array.Empty<Ipv4Address>(),
            Ipv4Mode == Ipv4Mode.Static ? BuildGateways(Gateways) : Array.Empty<Gateway>(),
            IsDnsStatic ? DnsMode.Static : DnsMode.Auto,
            IsDnsStatic
                ? BuildDns(DnsServers)
                : Array.Empty<string>(),
            MetricModeIndex switch
            {
                1 => MetricMode.Automatic,
                2 => MetricMode.Manual,
                _ => MetricMode.Keep
            },
            MetricModeIndex == 2 ? ParseInt(InterfaceMetric, 0) : null);

        Ipv6Desired ipv6 = Ipv6ModeIndex switch
        {
            1 => new Ipv6Desired(
                Ipv6Mode.Auto,
                Array.Empty<Ipv4Address>(),
                Array.Empty<Gateway>(),
                IsIpv6DnsStatic ? DnsMode.Static : DnsMode.Auto,
                IsIpv6DnsStatic ? BuildDns(Ipv6DnsServers) : Array.Empty<string>(),
                Ipv6MetricModeIndex switch
                {
                    1 => MetricMode.Automatic,
                    2 => MetricMode.Manual,
                    _ => MetricMode.Keep
                },
                Ipv6MetricModeIndex == 2 ? ParseInt(Ipv6InterfaceMetric, 0) : null),
            2 => new Ipv6Desired(
                Ipv6Mode.Static,
                BuildAddresses(Ipv6Addresses),
                BuildGateways(Ipv6Gateways),
                IsIpv6DnsStatic ? DnsMode.Static : DnsMode.Auto,
                IsIpv6DnsStatic ? BuildDns(Ipv6DnsServers) : Array.Empty<string>(),
                Ipv6MetricModeIndex switch
                {
                    1 => MetricMode.Automatic,
                    2 => MetricMode.Manual,
                    _ => MetricMode.Keep
                },
                Ipv6MetricModeIndex == 2 ? ParseInt(Ipv6InterfaceMetric, 0) : null),
            3 => new Ipv6Desired(
                Ipv6Mode.Disabled,
                Array.Empty<Ipv4Address>(),
                Array.Empty<Gateway>(),
                DnsMode.Auto,
                Array.Empty<string>(),
                MetricMode.Keep,
                null),
            _ => Ipv6Desired.Keep()
        };

        var wireless = WirelessActionIndex switch
        {
            1 => new WirelessDesired(WirelessAction.Connect, SelectedProfile),
            2 => new WirelessDesired(WirelessAction.Disconnect),
            _ => WirelessDesired.Keep()
        };

        return new DesiredState(ipv4, ipv6, wireless);
    }

    private void LoadDesired(DesiredState desired)
    {
        _loading = true;
        try
        {
            LoadDesiredCore(desired);
        }
        finally
        {
            _loading = false;
            IsDirty = false;
        }
    }

    private void LoadDesiredCore(DesiredState desired)
    {
        var ipv4 = desired.Ipv4;
        Ipv4Mode = ipv4.Mode;
        if (ipv4.Mode == Ipv4Mode.Static)
        {
            foreach (var address in ipv4.Addresses)
            {
                Addresses.Add(new AddressRowViewModel(row => Addresses.Remove(row))
                {
                    Address = address.Address,
                    PrefixLength = address.PrefixLength.ToString()
                });
            }

            foreach (var gateway in ipv4.Gateways)
            {
                Gateways.Add(new GatewayRowViewModel(row => Gateways.Remove(row))
                {
                    Address = gateway.Address,
                    Metric = gateway.Metric?.ToString() ?? string.Empty
                });
            }
        }

        if (Addresses.Count == 0)
        {
            AddAddress();
        }

        if (Gateways.Count == 0)
        {
            AddGateway();
        }

        IsDnsStatic = ipv4.DnsMode == DnsMode.Static;
        foreach (var dns in ipv4.DnsServers)
        {
            DnsServers.Add(new DnsRowViewModel(row => DnsServers.Remove(row)) { Address = dns });
        }

        if (DnsServers.Count == 0)
        {
            AddDns();
        }

        MetricModeIndex = ipv4.MetricMode switch
        {
            MetricMode.Automatic => 1,
            MetricMode.Manual => 2,
            _ => 0
        };
        InterfaceMetric = ipv4.InterfaceMetric?.ToString() ?? "25";

        var ipv6 = desired.Ipv6;
        Ipv6ModeIndex = ipv6?.Mode switch
        {
            Ipv6Mode.Auto => 1,
            Ipv6Mode.Static => 2,
            Ipv6Mode.Disabled => 3,
            _ => 0
        };

        if (ipv6 is not null && ipv6.Mode is Ipv6Mode.Static or Ipv6Mode.Auto)
        {
            if (ipv6.Mode == Ipv6Mode.Static)
            {
                foreach (var address in ipv6.Addresses)
                {
                    Ipv6Addresses.Add(new AddressRowViewModel(row => Ipv6Addresses.Remove(row))
                    {
                        Address = address.Address,
                        PrefixLength = address.PrefixLength.ToString()
                    });
                }

                foreach (var gateway in ipv6.Gateways)
                {
                    Ipv6Gateways.Add(new GatewayRowViewModel(row => Ipv6Gateways.Remove(row))
                    {
                        Address = gateway.Address,
                        Metric = gateway.Metric?.ToString() ?? string.Empty
                    });
                }
            }

            // DNS and metric are adapter-wide and can be static even in automatic mode.
            foreach (var dns in ipv6.DnsServers)
            {
                Ipv6DnsServers.Add(new DnsRowViewModel(row => Ipv6DnsServers.Remove(row)) { Address = dns });
            }

            IsIpv6DnsStatic = ipv6.DnsMode == DnsMode.Static;
            Ipv6MetricModeIndex = ipv6.MetricMode switch
            {
                MetricMode.Automatic => 1,
                MetricMode.Manual => 2,
                _ => 0
            };
            Ipv6InterfaceMetric = ipv6.InterfaceMetric?.ToString() ?? "25";
        }

        if (Ipv6Addresses.Count == 0)
        {
            AddIpv6Address();
        }

        if (Ipv6Gateways.Count == 0)
        {
            AddIpv6Gateway();
        }

        if (Ipv6DnsServers.Count == 0)
        {
            AddIpv6Dns();
        }

        var wireless = desired.Wireless;
        WirelessActionIndex = wireless?.Action switch
        {
            WirelessAction.Connect => 1,
            WirelessAction.Disconnect => 2,
            _ => 0
        };
        SelectedProfile = wireless?.SavedProfileName ?? WifiProfiles.FirstOrDefault();

        IsDirty = false;
    }

    private static IReadOnlyList<Ipv4Address> BuildAddresses(
        IEnumerable<AddressRowViewModel> rows) =>
        rows.Where(r => !string.IsNullOrWhiteSpace(r.Address))
            .Select(r => new Ipv4Address(r.Address.Trim(), ParseInt(r.PrefixLength, -1)))
            .ToArray();

    private static IReadOnlyList<Gateway> BuildGateways(
        IEnumerable<GatewayRowViewModel> rows) =>
        rows.Where(r => !string.IsNullOrWhiteSpace(r.Address))
            .Select(r => new Gateway(
                r.Address.Trim(),
                string.IsNullOrWhiteSpace(r.Metric) ? null : ParseInt(r.Metric, 0)))
            .ToArray();

    private static IReadOnlyList<string> BuildDns(
        IEnumerable<DnsRowViewModel> rows) =>
        rows.Where(r => !string.IsNullOrWhiteSpace(r.Address))
            .Select(r => r.Address.Trim())
            .ToArray();

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), out var value) ? value : fallback;
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetSwitch.Core.Models;

namespace NetSwitch.App.ViewModels;

public sealed class PresetItemViewModel : ObservableObject
{
    private Preset _preset;
    private bool _isCurrent;
    private DateTimeOffset? _lastAppliedAt;
    private bool _isLastApplied;

    public PresetItemViewModel(
        Preset preset,
        Action<PresetItemViewModel> test,
        Action<PresetItemViewModel> edit,
        Action<PresetItemViewModel> createFrom,
        Action<PresetItemViewModel> apply,
        Action<PresetItemViewModel> delete)
    {
        _preset = preset;
        TestCommand = new RelayCommand(() => test(this));
        EditCommand = new RelayCommand(() => edit(this));
        CreateFromCommand = new RelayCommand(() => createFrom(this));
        ApplyCommand = new RelayCommand(() => apply(this));
        DeleteCommand = new RelayCommand(() => delete(this));
    }

    public Preset Preset => _preset;

    public string Id => _preset.Id;

    public string Name => _preset.Name;

    public bool IsCurrent => _isCurrent;

    public bool IsStatic => _preset.Desired.Ipv4.Mode == Ipv4Mode.Static;

    public bool IsDhcp => !IsStatic;

    public string ModeText => IsStatic ? "静态" : "DHCP";

    public string SummaryText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_preset.Notes))
            {
                return _preset.Notes;
            }

            if (IsStatic)
            {
                var first = _preset.Desired.Ipv4.Addresses.FirstOrDefault();
                return first is null
                    ? "静态 IP"
                    : $"静态 IP · 主用 {first.Address}/{first.PrefixLength}";
            }

            return _preset.Desired.Ipv4.DnsMode == DnsMode.Static
                ? "DHCP · 仅覆盖 DNS 服务器"
                : "DHCP · DNS 自动 · 自动跃点";
        }
    }

    public string GatewayText
    {
        get
        {
            var desired = _preset.Desired.Ipv4;
            if (desired.Mode == Ipv4Mode.Dhcp)
            {
                return "由 DHCP 下发";
            }

            return DisplayFormat.Gateways(desired.Gateways);
        }
    }

    public string DnsText
    {
        get
        {
            var desired = _preset.Desired.Ipv4;
            return desired.DnsMode == DnsMode.Static
                ? DisplayFormat.Dns(desired.DnsServers, DnsMode.Static)
                : "由 DHCP 下发";
        }
    }

    public string Ipv6Text
    {
        get
        {
            var ipv6 = _preset.Desired.Ipv6;
            return ipv6?.Mode switch
            {
                null or Ipv6Mode.Keep => "保持系统状态",
                Ipv6Mode.Auto => "自动获取",
                Ipv6Mode.Disabled => "禁用 IPv6 绑定",
                Ipv6Mode.Static => DisplayFormat.Addresses(ipv6.Addresses),
                _ => "保持系统状态"
            };
        }
    }

    public string LastAppliedText
    {
        get
        {
            if (_isLastApplied)
            {
                return $"{DisplayFormat.RelativeTime(_lastAppliedAt)}应用";
            }

            return $"更新于 {DisplayFormat.RelativeTime(_preset.UpdatedAt)}";
        }
    }

    public IRelayCommand TestCommand { get; }

    public IRelayCommand EditCommand { get; }

    public IRelayCommand CreateFromCommand { get; }

    public IRelayCommand ApplyCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    public void Update(
        Preset preset,
        bool isCurrent,
        bool isLastApplied,
        DateTimeOffset? lastAppliedAt)
    {
        _preset = preset;
        _isCurrent = isCurrent;
        _isLastApplied = isLastApplied;
        _lastAppliedAt = lastAppliedAt;

        OnPropertyChanged(nameof(Preset));
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(IsStatic));
        OnPropertyChanged(nameof(IsDhcp));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(GatewayText));
        OnPropertyChanged(nameof(DnsText));
        OnPropertyChanged(nameof(Ipv6Text));
        OnPropertyChanged(nameof(LastAppliedText));
    }
}

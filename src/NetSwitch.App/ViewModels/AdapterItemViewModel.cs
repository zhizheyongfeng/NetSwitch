using CommunityToolkit.Mvvm.ComponentModel;
using NetSwitch.Core.Models;

namespace NetSwitch.App.ViewModels;

public sealed class AdapterItemViewModel : ObservableObject
{
    private AdapterState? _state;

    public AdapterItemViewModel(AdapterInfo info, AdapterState? state)
    {
        Info = info;
        _state = state;
    }

    public AdapterInfo Info { get; }

    public AdapterKey Key => Info.Key;

    public string Name => Info.Key.FriendlyName;

    public string Description => Info.Description;

    public bool IsUp => Info.IsUp;

    public bool IsWifi => Info.Kind == AdapterKind.WiFi;

    public bool IsVirtual => Info.Kind == AdapterKind.Virtual;

    public string StatusText => Info.IsUp ? "已连接" : "未连接";

    public string AddressText
    {
        get
        {
            if (!Info.IsUp)
            {
                return Info.Kind == AdapterKind.WiFi ? "无线网络未连接" : "网络电缆被拔出";
            }

            var address = _state?.Ipv4.Addresses.FirstOrDefault();
            if (address is null)
            {
                return "正在获取地址…";
            }

            var text = $"{address.Address}/{address.PrefixLength}";
            if (_state is { Ipv4.DhcpEnabled: true })
            {
                text += " · DHCP";
            }

            return text;
        }
    }

    public AdapterState? State => _state;

    public void Update(AdapterInfo info, AdapterState? state)
    {
        _state = state;
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(IsUp));
        OnPropertyChanged(nameof(IsWifi));
        OnPropertyChanged(nameof(IsVirtual));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(State));
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NetSwitch.App.ViewModels;

public sealed partial class AddressRowViewModel : ObservableObject
{
    public AddressRowViewModel(Action<AddressRowViewModel> remove)
    {
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private string _prefixLength = "24";

    public IRelayCommand RemoveCommand { get; }
}

public sealed partial class GatewayRowViewModel : ObservableObject
{
    public GatewayRowViewModel(Action<GatewayRowViewModel> remove)
    {
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private string _metric = string.Empty;

    public IRelayCommand RemoveCommand { get; }
}

public sealed partial class DnsRowViewModel : ObservableObject
{
    public DnsRowViewModel(Action<DnsRowViewModel> remove)
    {
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    [ObservableProperty]
    private string _address = string.Empty;

    public IRelayCommand RemoveCommand { get; }
}

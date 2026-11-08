using NetSwitch.Core.Models;

namespace NetSwitch.Platform.Fake;

/// <summary>
/// Shared in-memory state used by all fake platform services. Thread-safe so the UI
/// can read while an apply operation is simulating latency.
/// </summary>
public sealed class FakeNetworkState
{
    private readonly object _sync = new();
    private readonly List<AdapterInfo> _adapters = new();
    private readonly Dictionary<string, AdapterState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public static FakeNetworkState CreateDefault()
    {
        var state = new FakeNetworkState();

        var ethernet = new AdapterKey("fake", "fake-ethernet", "AA:BB:CC:00:00:01", "以太网");
        var wifi = new AdapterKey("fake", "fake-wifi", "AA:BB:CC:00:00:02", "WLAN");
        var ethernet2 = new AdapterKey("fake", "fake-ethernet-2", "AA:BB:CC:00:00:03", "以太网 2");

        state.AddAdapter(new AdapterInfo(
            ethernet,
            "Intel I219-V (Fake)",
            AdapterKind.Ethernet,
            IsUp: true,
            IsPhysical: true,
            InterfaceIndex: 1,
            Luid: 1));

        state.AddAdapter(new AdapterInfo(
            wifi,
            "Intel AX201 (Fake)",
            AdapterKind.WiFi,
            IsUp: true,
            IsPhysical: true,
            InterfaceIndex: 2,
            Luid: 2));

        state.AddAdapter(new AdapterInfo(
            ethernet2,
            "Realtek USB GbE (Fake)",
            AdapterKind.Ethernet,
            IsUp: false,
            IsPhysical: true,
            InterfaceIndex: 3,
            Luid: 3));

        state.SetState(CreateDhcpState(ethernet, "192.168.1.23", IsWifi: false));
        state.SetState(CreateDhcpState(wifi, "192.168.1.88", IsWifi: true));
        state.SetState(CreateDownState(ethernet2));

        state.SetProfiles(wifi, new[] { "家庭网络-5G", "公司-WiFi", "手机热点" });
        state.SetProfiles(ethernet, Array.Empty<string>());

        state.SetState(state.GetState(wifi)! with
        {
            Wireless = new WirelessState(true, "家庭网络-5G", "家庭网络-5G")
        });

        return state;
    }

    public IReadOnlyList<AdapterInfo> GetAdapters()
    {
        lock (_sync)
        {
            return _adapters.ToArray();
        }
    }

    public AdapterInfo? GetAdapter(AdapterKey key)
    {
        lock (_sync)
        {
            return _adapters.FirstOrDefault(a => KeyEquals(a.Key, key));
        }
    }

    public AdapterState? GetState(AdapterKey key)
    {
        lock (_sync)
        {
            return _states.TryGetValue(key.StableId, out var state) ? state : null;
        }
    }

    public void SetState(AdapterState state)
    {
        lock (_sync)
        {
            _states[state.Key.StableId] = state;
        }
    }

    public IReadOnlyList<string> GetProfiles(AdapterKey key)
    {
        lock (_sync)
        {
            return _profiles.TryGetValue(key.StableId, out var profiles)
                ? profiles.ToArray()
                : Array.Empty<string>();
        }
    }

    public void SetProfiles(AdapterKey key, IEnumerable<string> profiles)
    {
        lock (_sync)
        {
            _profiles[key.StableId] = profiles.Distinct(StringComparer.Ordinal).ToList();
        }
    }

    public void AddAdapter(AdapterInfo adapter)
    {
        lock (_sync)
        {
            var index = _adapters.FindIndex(a => KeyEquals(a.Key, adapter.Key));
            if (index >= 0)
            {
                _adapters[index] = adapter;
            }
            else
            {
                _adapters.Add(adapter);
            }
        }
    }

    public static bool KeyEquals(AdapterKey left, AdapterKey right) =>
        string.Equals(left.Platform, right.Platform, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.StableId, right.StableId, StringComparison.OrdinalIgnoreCase);

    private static AdapterState CreateDhcpState(AdapterKey key, string address, bool IsWifi)
    {
        var now = DateTimeOffset.Now;
        return new AdapterState(
            key,
            now,
            new Ipv4State(
                DhcpEnabled: true,
                Addresses: new[] { new Ipv4Address(address, 24) },
                Gateways: new[] { new Gateway("192.168.1.1") },
                DnsMode: DnsMode.Auto,
                DnsServers: new[] { "192.168.1.1" },
                AutomaticMetric: true,
                InterfaceMetric: 25),
            new Ipv6State(
                BindingEnabled: true,
                Addresses: new[]
                {
                    new Ipv4Address(IsWifi ? "fe80::a1b2:c3d4:e5f6:0002" : "fe80::a1b2:c3d4:e5f6:0001", 64),
                    new Ipv4Address(IsWifi ? "240e:3a1:2b3c:4d5e::88" : "240e:3a1:2b3c:4d5e::23", 64)
                },
                Gateways: new[] { new Gateway("fe80::1") },
                DnsServers: Array.Empty<string>(),
                AutomaticMetric: true,
                InterfaceMetric: 25),
            new WirelessState(false, null, null));
    }

    private static AdapterState CreateDownState(AdapterKey key)
    {
        return new AdapterState(
            key,
            DateTimeOffset.Now,
            new Ipv4State(
                DhcpEnabled: true,
                Addresses: Array.Empty<Ipv4Address>(),
                Gateways: Array.Empty<Gateway>(),
                DnsMode: DnsMode.Auto,
                DnsServers: Array.Empty<string>(),
                AutomaticMetric: true,
                InterfaceMetric: 25),
            new Ipv6State(
                BindingEnabled: true,
                Addresses: Array.Empty<Ipv4Address>(),
                Gateways: Array.Empty<Gateway>(),
                DnsServers: Array.Empty<string>(),
                AutomaticMetric: true,
                InterfaceMetric: 25),
            new WirelessState(false, null, null));
    }
}

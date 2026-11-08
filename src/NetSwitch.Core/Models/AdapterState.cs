namespace NetSwitch.Core.Models;

public sealed record AdapterState(
    AdapterKey Key,
    DateTimeOffset CapturedAt,
    Ipv4State Ipv4,
    Ipv6State Ipv6,
    WirelessState? Wireless);

public sealed record Ipv4State(
    bool DhcpEnabled,
    IReadOnlyList<Ipv4Address> Addresses,
    IReadOnlyList<Gateway> Gateways,
    DnsMode DnsMode,
    IReadOnlyList<string> DnsServers,
    bool AutomaticMetric,
    int InterfaceMetric);

/// <summary>
/// Live IPv6 state. <paramref name="Mode"/> and <paramref name="DnsMode"/> are detected by
/// the platform backend and let a snapshot be restored without guessing whether the
/// interface was static or automatic before the transaction.
/// </summary>
public sealed record Ipv6State(
    bool BindingEnabled,
    IReadOnlyList<Ipv4Address> Addresses,
    IReadOnlyList<Gateway> Gateways,
    IReadOnlyList<string> DnsServers,
    bool AutomaticMetric,
    int InterfaceMetric,
    DnsMode DnsMode = DnsMode.Auto,
    Ipv6Mode Mode = Ipv6Mode.Auto);

public sealed record WirelessState(
    bool IsConnected,
    string? ActiveProfileName,
    string? Ssid);

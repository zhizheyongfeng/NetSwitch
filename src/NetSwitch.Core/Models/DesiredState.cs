namespace NetSwitch.Core.Models;

/// <summary>
/// An address with a prefix length. Reused for IPv6; the address text determines the family.
/// </summary>
public sealed record Ipv4Address(string Address, int PrefixLength);

public sealed record Gateway(string Address, int? Metric = null);

public sealed record Ipv4Desired(
    Ipv4Mode Mode,
    IReadOnlyList<Ipv4Address> Addresses,
    IReadOnlyList<Gateway> Gateways,
    DnsMode DnsMode,
    IReadOnlyList<string> DnsServers,
    MetricMode MetricMode,
    int? InterfaceMetric)
{
    public static Ipv4Desired Dhcp() => new(
        Ipv4Mode.Dhcp,
        Array.Empty<Ipv4Address>(),
        Array.Empty<Gateway>(),
        DnsMode.Auto,
        Array.Empty<string>(),
        MetricMode.Automatic,
        null);
}

public sealed record Ipv6Desired(
    Ipv6Mode Mode,
    IReadOnlyList<Ipv4Address> Addresses,
    IReadOnlyList<Gateway> Gateways,
    DnsMode DnsMode,
    IReadOnlyList<string> DnsServers,
    MetricMode MetricMode,
    int? InterfaceMetric)
{
    public static Ipv6Desired Keep() => new(
        Ipv6Mode.Keep,
        Array.Empty<Ipv4Address>(),
        Array.Empty<Gateway>(),
        DnsMode.Auto,
        Array.Empty<string>(),
        MetricMode.Keep,
        null);

    public static Ipv6Desired Auto() => new(
        Ipv6Mode.Auto,
        Array.Empty<Ipv4Address>(),
        Array.Empty<Gateway>(),
        DnsMode.Auto,
        Array.Empty<string>(),
        MetricMode.Keep,
        null);

    public static Ipv6Desired Disabled() => new(
        Ipv6Mode.Disabled,
        Array.Empty<Ipv4Address>(),
        Array.Empty<Gateway>(),
        DnsMode.Auto,
        Array.Empty<string>(),
        MetricMode.Keep,
        null);
}

public sealed record WirelessDesired(
    WirelessAction Action,
    string? SavedProfileName = null)
{
    public static WirelessDesired Keep() => new(WirelessAction.Keep);
}

public sealed record DesiredState(
    Ipv4Desired Ipv4,
    Ipv6Desired? Ipv6 = null,
    WirelessDesired? Wireless = null)
{
    public static DesiredState Dhcp() => new(Ipv4Desired.Dhcp(), Ipv6Desired.Keep(), WirelessDesired.Keep());
}

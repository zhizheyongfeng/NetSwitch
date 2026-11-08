using System.Net;
using System.Net.Sockets;
using NetSwitch.Core.Models;

namespace NetSwitch.Core.Validation;

/// <summary>
/// Builds presets from live adapter state and clones existing presets. Kept in Core so the
/// state-to-desired mapping can be unit tested without a UI or a platform backend.
/// </summary>
public static class PresetFactory
{
    /// <summary>
    /// Captures everything that is currently configured on the adapter: IPv4, IPv6 (binding
    /// state, mode, addresses, gateways, DNS and metric) and Wi-Fi (the connected saved profile).
    /// </summary>
    public static Preset FromState(string name, AdapterState state, DateTimeOffset now) =>
        new(
            Guid.NewGuid().ToString("N"),
            name,
            string.Empty,
            DesiredFromState(state),
            now,
            now);

    /// <summary>
    /// Creates a new preset from another one: new id and timestamps, same desired state.
    /// The caller is responsible for choosing a unique name.
    /// </summary>
    public static Preset CloneAsNew(Preset source, string name, DateTimeOffset now) =>
        source with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            CreatedAt = now,
            UpdatedAt = now
        };

    public static DesiredState DesiredFromState(AdapterState state) =>
        new(
            Ipv4FromState(state.Ipv4),
            Ipv6FromState(state.Ipv6),
            WirelessFromState(state.Wireless));

    private static Ipv4Desired Ipv4FromState(Ipv4State ipv4)
    {
        var dnsServers = ipv4.DnsMode == DnsMode.Static
            ? ipv4.DnsServers.ToArray()
            : Array.Empty<string>();

        if (ipv4.DhcpEnabled)
        {
            return Ipv4Desired.Dhcp() with
            {
                DnsMode = ipv4.DnsMode,
                DnsServers = dnsServers
            };
        }

        return new Ipv4Desired(
            Ipv4Mode.Static,
            ipv4.Addresses.ToArray(),
            ipv4.Gateways.ToArray(),
            ipv4.DnsMode,
            dnsServers,
            ipv4.AutomaticMetric ? MetricMode.Automatic : MetricMode.Manual,
            ipv4.AutomaticMetric ? null : ipv4.InterfaceMetric);
    }

    private static Ipv6Desired Ipv6FromState(Ipv6State ipv6)
    {
        if (!ipv6.BindingEnabled)
        {
            return Ipv6Desired.Disabled();
        }

        var dnsServers = ipv6.DnsMode == DnsMode.Static
            ? ipv6.DnsServers.ToArray()
            : Array.Empty<string>();
        var metricMode = ipv6.AutomaticMetric ? MetricMode.Automatic : MetricMode.Manual;
        int? metric = ipv6.AutomaticMetric ? null : ipv6.InterfaceMetric;

        return ipv6.Mode switch
        {
            Ipv6Mode.Disabled => Ipv6Desired.Disabled(),
            Ipv6Mode.Static => new Ipv6Desired(
                Ipv6Mode.Static,
                FilterManualIpv6Addresses(ipv6.Addresses),
                ipv6.Gateways.ToArray(),
                ipv6.DnsMode,
                dnsServers,
                metricMode,
                metric),
            Ipv6Mode.Auto => new Ipv6Desired(
                Ipv6Mode.Auto,
                Array.Empty<Ipv4Address>(),
                Array.Empty<Gateway>(),
                ipv6.DnsMode,
                dnsServers,
                metricMode,
                metric),
            _ => Ipv6Desired.Keep()
        };
    }

    /// <summary>
    /// Link-local addresses (fe80::/10) are created by Windows on every interface and are not
    /// meaningful in a preset; keeping them would make read-back comparisons unstable.
    /// </summary>
    private static Ipv4Address[] FilterManualIpv6Addresses(IReadOnlyList<Ipv4Address> addresses) =>
        addresses
            .Where(a => !IsLinkLocal(a.Address))
            .ToArray();

    private static bool IsLinkLocal(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        return parsed.IsIPv6LinkLocal;
    }

    private static WirelessDesired WirelessFromState(WirelessState? wireless)
    {
        if (wireless is { IsConnected: true, ActiveProfileName: { Length: > 0 } profile })
        {
            return new WirelessDesired(WirelessAction.Connect, profile);
        }

        return WirelessDesired.Keep();
    }
}

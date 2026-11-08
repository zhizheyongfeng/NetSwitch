using NetSwitch.Core.Models;

namespace NetSwitch.Core.Validation;

/// <summary>
/// Normalized comparison between a preset's managed fields and the actual adapter state.
/// Fields in Keep mode are intentionally ignored.
/// </summary>
public static class PresetFingerprint
{
    public static bool Matches(DesiredState desired, AdapterState state)
    {
        if (!MatchesIpv4(desired.Ipv4, state.Ipv4))
        {
            return false;
        }

        if (desired.Ipv6 is not null && desired.Ipv6.Mode != Ipv6Mode.Keep &&
            !MatchesIpv6(desired.Ipv6, state.Ipv6))
        {
            return false;
        }

        return desired.Wireless is null ||
               desired.Wireless.Action == WirelessAction.Keep ||
               MatchesWireless(desired.Wireless, state.Wireless);
    }

    public static bool MatchesIpv4(Ipv4Desired desired, Ipv4State state)
    {
        if (desired.Mode == Ipv4Mode.Dhcp)
        {
            if (!state.DhcpEnabled)
            {
                return false;
            }
        }
        else
        {
            if (state.DhcpEnabled)
            {
                return false;
            }

            if (!AddressSetsEqual(desired.Addresses, state.Addresses))
            {
                return false;
            }

            if (!GatewaySetsEqual(desired.Gateways, state.Gateways))
            {
                return false;
            }
        }

        if (!DnsMatches(desired.DnsMode, desired.DnsServers, state.DnsMode, state.DnsServers))
        {
            return false;
        }

        return MetricMatches(desired.MetricMode, desired.InterfaceMetric, state.AutomaticMetric, state.InterfaceMetric);
    }

    public static bool MatchesIpv6(Ipv6Desired desired, Ipv6State state)
    {
        return desired.Mode switch
        {
            Ipv6Mode.Keep => true,
            Ipv6Mode.Disabled => !state.BindingEnabled,
            Ipv6Mode.Auto => state.BindingEnabled
                && state.Mode != Ipv6Mode.Static
                && (desired.DnsMode == DnsMode.Auto ||
                    DnsMatches(desired.DnsMode, desired.DnsServers, state.DnsMode, state.DnsServers))
                && MetricMatches(desired.MetricMode, desired.InterfaceMetric, state.AutomaticMetric, state.InterfaceMetric),
            Ipv6Mode.Static => state.BindingEnabled
                && AddressSetsEqual(
                    desired.Addresses,
                    Ipv6AddressUtil.ComparableAddresses(desired.Addresses, state.Addresses))
                && GatewaySetsEqual(desired.Gateways, state.Gateways)
                && DnsMatches(desired.DnsMode, desired.DnsServers, state.DnsMode, state.DnsServers)
                && MetricMatches(desired.MetricMode, desired.InterfaceMetric, state.AutomaticMetric, state.InterfaceMetric),
            _ => false
        };
    }

    public static bool MatchesWireless(WirelessDesired desired, WirelessState? state)
    {
        if (desired.Action == WirelessAction.Keep)
        {
            return true;
        }

        if (desired.Action == WirelessAction.Disconnect)
        {
            return state is not { IsConnected: true };
        }

        return state is { IsConnected: true } &&
               string.Equals(state.ActiveProfileName, desired.SavedProfileName, StringComparison.Ordinal);
    }

    private static bool DnsMatches(
        DnsMode desiredMode,
        IReadOnlyList<string> desiredServers,
        DnsMode actualMode,
        IReadOnlyList<string> actualServers)
    {
        if (desiredMode == DnsMode.Auto)
        {
            return actualMode == DnsMode.Auto;
        }

        return actualMode == DnsMode.Static && desiredServers.SequenceEqual(actualServers, StringComparer.OrdinalIgnoreCase);
    }

    private static bool MetricMatches(
        MetricMode mode,
        int? desiredMetric,
        bool actualAutomatic,
        int actualMetric)
    {
        return mode switch
        {
            MetricMode.Keep => true,
            MetricMode.Automatic => actualAutomatic,
            MetricMode.Manual => !actualAutomatic && desiredMetric == actualMetric,
            _ => false
        };
    }

    private static bool AddressSetsEqual(
        IReadOnlyList<Ipv4Address> desired,
        IReadOnlyList<Ipv4Address> actual)
    {
        var left = desired
            .Select(a => $"{a.Address}/{a.PrefixLength}")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var right = actual
            .Select(a => $"{a.Address}/{a.PrefixLength}")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        return left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
    }

    private static bool GatewaySetsEqual(
        IReadOnlyList<Gateway> desired,
        IReadOnlyList<Gateway> actual)
    {
        var left = desired
            .Select(g => $"{g.Address}|{g.Metric}")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var right = actual
            .Select(g => $"{g.Address}|{g.Metric}")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        return left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
    }
}

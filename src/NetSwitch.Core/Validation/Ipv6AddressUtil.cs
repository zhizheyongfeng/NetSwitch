using System.Net;
using System.Net.Sockets;
using NetSwitch.Core.Models;

namespace NetSwitch.Core.Validation;

/// <summary>
/// IPv6 comparisons must ignore the automatic link-local address (and IPv4-mapped noise)
/// that every interface always has. A link-local address is only compared when the preset
/// explicitly lists it.
/// </summary>
public static class Ipv6AddressUtil
{
    public static bool IsGlobalUnicast(string address) =>
        IPAddress.TryParse(address, out var parsed) && IsGlobalUnicast(parsed);

    public static bool IsGlobalUnicast(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 &&
        !address.Equals(IPAddress.IPv6Any) &&
        !address.Equals(IPAddress.IPv6Loopback) &&
        !address.IsIPv6LinkLocal &&
        !address.IsIPv6Multicast &&
        !address.IsIPv4MappedToIPv6;

    /// <summary>
    /// Returns the actual addresses that should participate in a static comparison: global
    /// addresses plus any address the preset explicitly asks for (for example a link-local).
    /// </summary>
    public static IReadOnlyList<Ipv4Address> ComparableAddresses(
        IReadOnlyList<Ipv4Address> expected,
        IReadOnlyList<Ipv4Address> actual)
    {
        var explicitlyExpected = expected
            .Select(Format)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return actual
            .Where(address => IsGlobalUnicast(address.Address) ||
                              explicitlyExpected.Contains(Format(address)))
            .ToArray();
    }

    public static string Format(Ipv4Address address) => $"{address.Address}/{address.PrefixLength}";
}

using System.Net;
using Microsoft.Win32;
using NetSwitch.Core.Models;

namespace NetSwitch.Platform.Windows.Native;

/// <summary>
/// Read-only access to the per-interface TCP/IP registry values. Windows does not expose
/// "is DNS static or DHCP-provided" through the IP Helper API, so this is the reliable
/// source for that single bit of state. The app never writes the registry.
/// </summary>
internal static class TcpipRegistry
{
    private const string InterfacesRoot =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    private const string InterfacesRootV6 =
        @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces";

    /// <summary>Returns the statically configured DNS servers, or null when DNS is automatic.</summary>
    public static IReadOnlyList<string>? ReadStaticDnsServers(AdapterKey key, bool ipv6 = false)
    {
        var raw = ReadString(key, ipv6 ? InterfacesRootV6 : InterfacesRoot, "NameServer");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var servers = raw
            .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(value => IPAddress.TryParse(value, out _))
            .ToArray();

        return servers.Length == 0 ? null : servers;
    }

    private static string? ReadString(AdapterKey key, string root, string name)
    {
        try
        {
            using var interfaceKey = Registry.LocalMachine.OpenSubKey(
                $@"{root}\{key.StableId}");
            return interfaceKey?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                       or UnauthorizedAccessException
                                       or IOException)
        {
            return null;
        }
    }
}

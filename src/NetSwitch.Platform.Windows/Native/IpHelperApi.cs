using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using NetSwitch.Core.Models;

namespace NetSwitch.Platform.Windows.Native;

/// <summary>
/// Thin wrapper around the IP Helper API. Every public method assumes it is running on
/// Windows; callers check <see cref="OperatingSystem.IsWindows"/> first.
/// </summary>
internal static class IpHelperApi
{
    private const uint InitialBufferSize = 16 * 1024;
    private const int MaxBufferAttempts = 4;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetAdaptersAddresses(
        uint family,
        uint flags,
        IntPtr reserved,
        IntPtr addresses,
        ref uint sizePointer);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetIpInterfaceEntry(ref MibIpInterfaceRow row);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetIpInterfaceEntry(ref MibIpInterfaceRow row);

    [DllImport("dnsapi.dll", SetLastError = true)]
    private static extern uint DnsFlushResolverCache();

    public static IReadOnlyList<NativeAdapterSnapshot> GetAdapters()
    {
        var buffer = IntPtr.Zero;
        var size = InitialBufferSize;
        uint result;

        try
        {
            buffer = Marshal.AllocHGlobal((int)size);
            for (var attempt = 0; ; attempt++)
            {
                result = GetAdaptersAddresses(
                    NativeConstants.AF_UNSPEC,
                    NativeConstants.GAA_FLAG_SKIP_ANYCAST |
                    NativeConstants.GAA_FLAG_SKIP_MULTICAST |
                    NativeConstants.GAA_FLAG_INCLUDE_GATEWAYS,
                    IntPtr.Zero,
                    buffer,
                    ref size);

                if (result != NativeConstants.ERROR_BUFFER_OVERFLOW)
                {
                    break;
                }

                if (attempt >= MaxBufferAttempts)
                {
                    throw new InvalidOperationException("读取网络适配器列表失败：缓冲区持续增长。");
                }

                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal((int)size);
            }

            if (result != NativeConstants.NO_ERROR)
            {
                throw new Win32Exception((int)result, "GetAdaptersAddresses 失败。");
            }

            var snapshots = new List<NativeAdapterSnapshot>();
            var current = buffer;
            while (current != IntPtr.Zero)
            {
                var native = Marshal.PtrToStructure<IpAdapterAddresses>(current);
                var snapshot = TryBuildSnapshot(current, native);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }

                current = native.Next;
            }

            return snapshots;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public static (bool UseAutomatic, int Metric)? GetInterfaceMetric(
        ulong luid,
        uint interfaceIndex,
        ushort family)
    {
        var row = MibIpInterfaceRow.Create(family, luid, interfaceIndex);
        var result = GetIpInterfaceEntry(ref row);
        if (result != NativeConstants.NO_ERROR)
        {
            return null;
        }

        return (row.UseAutomaticMetric != 0, unchecked((int)row.Metric));
    }

    /// <summary>
    /// Sets the interface metric through the IP Helper API (the same setting the Windows
    /// adapter dialog calls "interface metric"). Returns false when the entry cannot be
    /// updated, so the caller can surface a verification failure.
    /// </summary>
    public static bool SetInterfaceMetric(
        ushort family,
        ulong luid,
        uint interfaceIndex,
        bool automatic,
        int metric,
        out string? error)
    {
        var row = MibIpInterfaceRow.Create(family, luid, interfaceIndex);
        var result = GetIpInterfaceEntry(ref row);
        if (result != NativeConstants.NO_ERROR)
        {
            error = new Win32Exception((int)result, "读取接口跃点失败。").Message;
            return false;
        }

        row.UseAutomaticMetric = (byte)(automatic ? 1 : 0);
        if (!automatic)
        {
            row.Metric = unchecked((uint)Math.Clamp(metric, 0, 9999));
        }

        result = SetIpInterfaceEntry(ref row);
        if (result != NativeConstants.NO_ERROR)
        {
            error = new Win32Exception((int)result, "写入接口跃点失败。").Message;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Enables or disables router discovery and DHCPv6 configuration on an IPv6 interface.
    /// Static IPv6 presets disable both so RAs cannot inject addresses after the write.
    /// </summary>
    public static bool SetIpv6RouterDiscovery(
        ulong luid,
        uint interfaceIndex,
        bool enabled,
        out string? error)
    {
        var row = MibIpInterfaceRow.Create(NativeConstants.AF_INET6, luid, interfaceIndex);
        var result = GetIpInterfaceEntry(ref row);
        if (result != NativeConstants.NO_ERROR)
        {
            error = new Win32Exception((int)result, "读取 IPv6 接口设置失败。").Message;
            return false;
        }

        row.RouterDiscoveryBehavior = enabled
            ? NativeConstants.ROUTER_DISCOVERY_ENABLED
            : NativeConstants.ROUTER_DISCOVERY_DISABLED;
        row.ManagedAddressConfigurationSupported = (byte)(enabled ? 1 : 0);
        row.OtherStatefulConfigurationSupported = (byte)(enabled ? 1 : 0);

        result = SetIpInterfaceEntry(ref row);
        if (result != NativeConstants.NO_ERROR)
        {
            error = new Win32Exception((int)result, "写入 IPv6 路由发现设置失败。").Message;
            return false;
        }

        error = null;
        return true;
    }

    public static void FlushDnsCache()
    {
        try
        {
            DnsFlushResolverCache();
        }
        catch (DllNotFoundException)
        {
            // Only possible on non-Windows hosts; callers already guard.
        }
    }

    private static NativeAdapterSnapshot? TryBuildSnapshot(
        IntPtr current,
        IpAdapterAddresses native)
    {
        if (native.IfType is NativeConstants.IF_TYPE_SOFTWARE_LOOPBACK
            or NativeConstants.IF_TYPE_TUNNEL)
        {
            return null;
        }

        var guid = SockaddrReader.ReadString(native.AdapterName, unicode: false);
        if (string.IsNullOrWhiteSpace(guid))
        {
            return null;
        }

        var friendlyName = SockaddrReader.ReadString(native.FriendlyName, unicode: true)?.Trim();
        var description = SockaddrReader.ReadString(native.Description, unicode: true)?.Trim();
        if (string.IsNullOrEmpty(friendlyName) && string.IsNullOrEmpty(description))
        {
            return null;
        }

        var interfaceIndex = unchecked((uint)Marshal.ReadInt32(current, 0));
        if (interfaceIndex == 0)
        {
            return null;
        }

        var mac = FormatMac(native.PhysicalAddress, native.PhysicalAddressLength);
        var isVirtual = VirtualAdapterDetector.IsVirtual(description, friendlyName, native.IfType);
        var key = new AdapterKey(
            "windows",
            guid,
            mac,
            string.IsNullOrWhiteSpace(friendlyName) ? description! : friendlyName!);

        var snapshot = new NativeAdapterSnapshot
        {
            Key = key,
            Description = string.IsNullOrWhiteSpace(description) ? key.FriendlyName : description!,
            IfType = native.IfType,
            IsUp = native.OperStatus == NativeConstants.IF_OPER_STATUS_UP,
            IsVirtual = isVirtual,
            InterfaceIndex = interfaceIndex,
            Luid = native.Luid,
            DhcpEnabled = (native.Flags & IpAdapterFlags.Dhcpv4Enabled) != 0,
            Ipv4Enabled = (native.Flags & IpAdapterFlags.Ipv4Enabled) != 0,
            Ipv6Enabled = (native.Flags & IpAdapterFlags.Ipv6Enabled) != 0,
            Ipv4Metric = unchecked((int)native.Ipv4Metric),
            Ipv6Metric = unchecked((int)native.Ipv6Metric)
        };

        var address = native.FirstUnicastAddress;
        while (address != IntPtr.Zero)
        {
            var unicast = Marshal.PtrToStructure<IpAdapterUnicastAddress>(address);
            var ip = SockaddrReader.ReadAddress(unicast.Address.lpSockaddr);
            if (ip is not null && unicast.DadState != NativeConstants.DAD_STATE_INVALID)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    snapshot.Ipv4Addresses.Add(new NativeUnicastAddress(
                        ip,
                        unicast.OnLinkPrefixLength,
                        unicast.PrefixOrigin,
                        unicast.SuffixOrigin));
                }
                else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    snapshot.Ipv6Addresses.Add(new NativeUnicastAddress(
                        ip,
                        unicast.OnLinkPrefixLength,
                        unicast.PrefixOrigin,
                        unicast.SuffixOrigin));
                }
            }

            address = unicast.Next;
        }

        ReadAddresses(native.FirstGatewayAddress, snapshot.Gateways);
        ReadAddresses(native.FirstDnsServerAddress, snapshot.DnsServers);

        return snapshot;
    }

    private static void ReadAddresses(IntPtr first, List<IPAddress> target)
    {
        var current = first;
        while (current != IntPtr.Zero)
        {
            var entry = Marshal.PtrToStructure<IpAdapterAddressEntry>(current);
            var ip = SockaddrReader.ReadAddress(entry.Address.lpSockaddr);
            if (ip is not null)
            {
                target.Add(ip);
            }

            current = entry.Next;
        }
    }

    private static string FormatMac(byte[]? value, uint length)
    {
        if (value is null || length == 0)
        {
            return string.Empty;
        }

        var count = (int)Math.Min(length, (uint)value.Length);
        return string.Join("-", value.Take(count).Select(b => b.ToString("X2")));
    }
}

internal readonly record struct NativeUnicastAddress(
    IPAddress Address,
    int PrefixLength,
    int PrefixOrigin,
    int SuffixOrigin);

internal sealed class NativeAdapterSnapshot
{
    public required AdapterKey Key { get; init; }

    public required string Description { get; init; }

    public required uint IfType { get; init; }

    public required bool IsUp { get; init; }

    public required bool IsVirtual { get; init; }

    public required uint InterfaceIndex { get; init; }

    public required ulong Luid { get; init; }

    public required bool DhcpEnabled { get; init; }

    public required bool Ipv4Enabled { get; init; }

    public required bool Ipv6Enabled { get; init; }

    public required int Ipv4Metric { get; init; }

    public required int Ipv6Metric { get; init; }

    public List<NativeUnicastAddress> Ipv4Addresses { get; } = new();

    public List<NativeUnicastAddress> Ipv6Addresses { get; } = new();

    public List<IPAddress> Gateways { get; } = new();

    public List<IPAddress> DnsServers { get; } = new();

    public AdapterKind Kind =>
        IsVirtual ? AdapterKind.Virtual
        : IfType is NativeConstants.IF_TYPE_IEEE80211 ? AdapterKind.WiFi
        : IfType is NativeConstants.IF_TYPE_ETHERNET_CSMACD ? AdapterKind.Ethernet
        : AdapterKind.Other;

    public bool IsPhysical => !IsVirtual && Key.MacAddress.Length > 0;

    public AdapterInfo ToInfo() => new(
        Key,
        Description,
        Kind,
        IsUp,
        IsPhysical,
        InterfaceIndex,
        Luid);
}

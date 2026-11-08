using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetSwitch.Core.Models;

namespace NetSwitch.Platform.Windows.Native;

/// <summary>
/// IPv6 address, default-route and DNS writes through the IP Helper API. The struct
/// layouts come from netioapi.h; offsets are pinned by unit tests so a mistake shows up
/// on the Linux build instead of only on real Windows hardware.
/// </summary>
internal static unsafe class Ipv6NativeApi
{
    private const uint InfiniteLifetime = 0xFFFFFFFF;
    private const uint DnsSettingIPv6 = 0x0001;
    private const uint DnsSettingNameServer = 0x0002;
    private const uint DnsSettingsVersion1 = 1;

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint CreateUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint DeleteUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern void InitializeUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetUnicastIpAddressTable(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint CreateIpForwardEntry2(ref MibIpForwardRow2 row);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint DeleteIpForwardEntry2(ref MibIpForwardRow2 row);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern void InitializeIpForwardEntry(ref MibIpForwardRow2 row);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern void FreeMibTable(IntPtr table);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint SetInterfaceDnsSettings(ref Guid interfaceGuid, ref DnsInterfaceSettings settings);

    public static void AddUnicastAddress(ulong interfaceLuid, uint interfaceIndex, Ipv4Address address)
    {
        if (!IPAddress.TryParse(address.Address, out var parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            throw new ArgumentException($"不是有效的 IPv6 地址：{address.Address}", nameof(address));
        }

        var row = new MibUnicastIpAddressRow();
        InitializeUnicastIpAddressEntry(ref row);
        row.Address = SockaddrInet.FromAddress(parsed);
        row.InterfaceLuid = interfaceLuid;
        row.InterfaceIndex = interfaceIndex;
        row.PrefixOrigin = NativeConstants.IP_PREFIX_ORIGIN_MANUAL;
        row.SuffixOrigin = NativeConstants.IP_SUFFIX_ORIGIN_MANUAL;
        row.ValidLifetime = InfiniteLifetime;
        row.PreferredLifetime = InfiniteLifetime;
        row.OnLinkPrefixLength = (byte)Math.Clamp(address.PrefixLength, 0, 128);

        var status = CreateUnicastIpAddressEntry(ref row);
        if (status is NativeConstants.NO_ERROR or NativeConstants.ERROR_OBJECT_ALREADY_EXISTS
            or NativeConstants.ERROR_FILE_EXISTS)
        {
            return;
        }

        throw NativeError(status, $"添加 IPv6 地址 {address.Address}/{address.PrefixLength} 失败");
    }

    public static void AddDefaultRoute(ulong interfaceLuid, uint interfaceIndex, Gateway gateway)
    {
        if (!IPAddress.TryParse(gateway.Address, out var parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            throw new ArgumentException($"不是有效的 IPv6 网关：{gateway.Address}", nameof(gateway));
        }

        var row = new MibIpForwardRow2();
        InitializeIpForwardEntry(ref row);
        row.InterfaceLuid = interfaceLuid;
        row.InterfaceIndex = interfaceIndex;
        row.DestinationPrefix.Prefix = SockaddrInet.FromAddress(IPAddress.IPv6Any);
        row.DestinationPrefix.PrefixLength = 0;
        row.NextHop = SockaddrInet.FromAddress(parsed);
        row.Metric = (uint)Math.Clamp(gateway.Metric ?? 0, 0, 9999);
        row.Protocol = NativeConstants.ROUTE_PROTOCOL_NET_MGMT;
        row.Origin = NativeConstants.ROUTE_ORIGIN_MANUAL;
        row.ValidLifetime = InfiniteLifetime;
        row.PreferredLifetime = InfiniteLifetime;

        var status = CreateIpForwardEntry2(ref row);
        if (status is NativeConstants.NO_ERROR or NativeConstants.ERROR_OBJECT_ALREADY_EXISTS
            or NativeConstants.ERROR_FILE_EXISTS)
        {
            return;
        }

        throw NativeError(status, $"添加 IPv6 默认网关 {gateway.Address} 失败");
    }

    /// <summary>
    /// Deletes addresses that belong to the adapter. <paramref name="manualOnly"/> is used
    /// when switching to automatic mode so RA/DHCPv6 addresses are left alone; well-known
    /// addresses (for example 6to4) are never touched.
    /// </summary>
    public static void DeleteGlobalUnicastAddresses(uint interfaceIndex, bool manualOnly)
    {
        foreach (var row in GetUnicastAddresses(interfaceIndex))
        {
            var address = row.Address.ToAddress();
            if (address is null || !IsGlobal(address))
            {
                continue;
            }

            if (row.PrefixOrigin == NativeConstants.IP_PREFIX_ORIGIN_WELL_KNOWN)
            {
                continue;
            }

            if (manualOnly &&
                (row.PrefixOrigin != NativeConstants.IP_PREFIX_ORIGIN_MANUAL ||
                 row.SuffixOrigin != NativeConstants.IP_SUFFIX_ORIGIN_MANUAL))
            {
                continue;
            }

            var rowToDelete = row;
            var status = DeleteUnicastIpAddressEntry(ref rowToDelete);
            if (status is NativeConstants.NO_ERROR or NativeConstants.ERROR_NOT_FOUND)
            {
                continue;
            }

            throw NativeError(status, $"删除原有 IPv6 地址 {address} 失败");
        }
    }

    /// <summary>
    /// Deletes default routes on the adapter. In automatic mode only manually added
    /// (NetMgmt) routes are removed so router-advertised routes can take over again.
    /// </summary>
    public static void DeleteDefaultRoutes(uint interfaceIndex, bool netMgmtOnly)
    {
        foreach (var row in GetForwardRows(interfaceIndex))
        {
            if (row.DestinationPrefix.PrefixLength != 0)
            {
                continue;
            }

            var destination = row.DestinationPrefix.Prefix.ToAddress();
            if (destination is null || !destination.Equals(IPAddress.IPv6Any))
            {
                continue;
            }

            if (netMgmtOnly && row.Protocol != NativeConstants.ROUTE_PROTOCOL_NET_MGMT)
            {
                continue;
            }

            var rowToDelete = row;
            var status = DeleteIpForwardEntry2(ref rowToDelete);
            if (status is NativeConstants.NO_ERROR or NativeConstants.ERROR_NOT_FOUND)
            {
                continue;
            }

            throw NativeError(status, "删除原有 IPv6 默认路由失败");
        }
    }

    /// <summary>
    /// Sets (or clears, when <paramref name="servers"/> is null/empty) the IPv6 DNS servers
    /// for one adapter. Clearing is verified against the read-only registry because the API
    /// does not document whether a null NameServer means "back to DHCP".
    /// </summary>
    public static void SetDns(string adapterStableId, IReadOnlyList<string>? servers)
    {
        if (!Guid.TryParse(adapterStableId, out var interfaceGuid))
        {
            throw new ArgumentException($"适配器标识不是 GUID：{adapterStableId}", nameof(adapterStableId));
        }

        var value = servers is { Count: > 0 }
            ? string.Join(",", servers)
            : null;

        ApplyDns(interfaceGuid, value);
        if (value is not null)
        {
            return;
        }

        var adapterKey = new AdapterKey("windows", adapterStableId, string.Empty, string.Empty);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (TcpipRegistry.ReadStaticDnsServers(adapterKey, ipv6: true) is null)
            {
                return;
            }

            Thread.Sleep(100);
        }

        // Some Windows builds keep an empty static list instead of deleting the value.
        ApplyDns(interfaceGuid, string.Empty);
    }

    private static void ApplyDns(Guid interfaceGuid, string? nameServer)
    {
        var settings = new DnsInterfaceSettings
        {
            Version = DnsSettingsVersion1,
            Flags = DnsSettingIPv6 | DnsSettingNameServer,
            NameServer = nameServer is null
                ? IntPtr.Zero
                : Marshal.StringToCoTaskMemUni(nameServer)
        };

        try
        {
            var status = SetInterfaceDnsSettings(ref interfaceGuid, ref settings);
            if (status != NativeConstants.NO_ERROR)
            {
                throw NativeError(status, "写入 IPv6 DNS 服务器失败");
            }
        }
        finally
        {
            if (settings.NameServer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(settings.NameServer);
            }
        }
    }

    private static IReadOnlyList<MibUnicastIpAddressRow> GetUnicastAddresses(uint interfaceIndex)
    {
        var status = GetUnicastIpAddressTable((ushort)NativeConstants.AF_INET6, out var table);
        if (status == NativeConstants.ERROR_NOT_FOUND)
        {
            return Array.Empty<MibUnicastIpAddressRow>();
        }

        if (status != NativeConstants.NO_ERROR)
        {
            throw NativeError(status, "读取 IPv6 地址表失败");
        }

        try
        {
            return ReadTable<MibUnicastIpAddressRow>(table, row => row.InterfaceIndex == interfaceIndex);
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    private static IReadOnlyList<MibIpForwardRow2> GetForwardRows(uint interfaceIndex)
    {
        var status = GetIpForwardTable2((ushort)NativeConstants.AF_INET6, out var table);
        if (status == NativeConstants.ERROR_NOT_FOUND)
        {
            return Array.Empty<MibIpForwardRow2>();
        }

        if (status != NativeConstants.NO_ERROR)
        {
            throw NativeError(status, "读取 IPv6 路由表失败");
        }

        try
        {
            return ReadTable<MibIpForwardRow2>(table, row => row.InterfaceIndex == interfaceIndex);
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    private static IReadOnlyList<T> ReadTable<T>(IntPtr table, Func<T, bool> predicate)
        where T : struct
    {
        // MIB tables start with a ULONG count followed by an array padded to the row
        // alignment (8 bytes for both rows used here).
        var count = unchecked((uint)Marshal.ReadInt32(table));
        var rowSize = Marshal.SizeOf<T>();
        var rows = new List<T>((int)Math.Min(count, 1024));
        for (var i = 0u; i < count; i++)
        {
            var rowPointer = IntPtr.Add(table, 8 + (int)(i * (uint)rowSize));
            var row = Marshal.PtrToStructure<T>(rowPointer);
            if (predicate(row))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static bool IsGlobal(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 &&
        !address.Equals(IPAddress.IPv6Any) &&
        !address.Equals(IPAddress.IPv6Loopback) &&
        !address.IsIPv6LinkLocal &&
        !address.IsIPv6Multicast &&
        !address.IsIPv4MappedToIPv6;

    private static Exception NativeError(uint status, string message)
    {
        var detail = new Win32Exception((int)status).Message;
        return new InvalidOperationException($"{message}：{detail}（错误 {status}）");
    }

    /// <summary>
    /// SOCKADDR_INET is a 28-byte union. Windows is little-endian, so storing the address
    /// bytes through little-endian integer fields reproduces the native byte order exactly.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 28, Pack = 4)]
    internal struct SockaddrInet
    {
        [FieldOffset(0)]
        public ushort Family;

        [FieldOffset(2)]
        public ushort Port;

        /// <summary>IPv4 address bytes (also the IPv6 flow-info field).</summary>
        [FieldOffset(4)]
        public uint AddressV4;

        [FieldOffset(8)]
        public ulong AddressV6Low;

        [FieldOffset(16)]
        public ulong AddressV6High;

        [FieldOffset(24)]
        public uint ScopeId;

        public static SockaddrInet FromAddress(IPAddress address)
        {
            var value = new SockaddrInet
            {
                Family = (ushort)(address.AddressFamily == AddressFamily.InterNetwork
                    ? NativeConstants.AF_INET
                    : NativeConstants.AF_INET6)
            };

            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                if (bytes.Length != 4)
                {
                    throw new ArgumentException("IPv4 地址长度无效。", nameof(address));
                }

                value.AddressV4 = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (bytes.Length != 16)
                {
                    throw new ArgumentException("IPv6 地址长度无效。", nameof(address));
                }

                value.AddressV6Low = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, 8));
                value.AddressV6High = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8, 8));
                value.ScopeId = unchecked((uint)address.ScopeId);
            }
            else
            {
                throw new ArgumentException("只支持 IPv4/IPv6 地址。", nameof(address));
            }

            return value;
        }

        public readonly IPAddress? ToAddress()
        {
            if (Family == NativeConstants.AF_INET)
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, AddressV4);
                return new IPAddress(bytes);
            }

            if (Family == NativeConstants.AF_INET6)
            {
                var bytes = new byte[16];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), AddressV6Low);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8, 8), AddressV6High);
                return new IPAddress(bytes, ScopeId);
            }

            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct IpAddressPrefix
    {
        public SockaddrInet Prefix;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Sequential, Size = 80)]
    internal struct MibUnicastIpAddressRow
    {
        public SockaddrInet Address;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public int PrefixOrigin;
        public int SuffixOrigin;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public byte OnLinkPrefixLength;
        public byte SkipAsSource;
        public int DadState;
        public uint ScopeId;
        public long CreationTimeStamp;
    }

    [StructLayout(LayoutKind.Sequential, Size = 104)]
    internal struct MibIpForwardRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IpAddressPrefix DestinationPrefix;
        public SockaddrInet NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public int Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public int Origin;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DnsInterfaceSettings
    {
        public uint Version;
        public ulong Flags;
        public IntPtr Domain;
        public IntPtr NameServer;
        public IntPtr SearchList;
        public uint RegistrationEnabled;
        public uint RegisterAdapterName;
        public uint EnableLlmNR;
        public uint QueryAdapterName;
        public IntPtr ProfileNameServer;
    }
}

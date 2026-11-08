using System.Net;
using System.Runtime.InteropServices;

namespace NetSwitch.Platform.Windows.Native;

internal static class NativeConstants
{
    public const int AF_UNSPEC = 0;
    public const int AF_INET = 2;
    public const int AF_INET6 = 23;

    public const uint GAA_FLAG_SKIP_ANYCAST = 0x0002;
    public const uint GAA_FLAG_SKIP_MULTICAST = 0x0004;
    public const uint GAA_FLAG_INCLUDE_GATEWAYS = 0x0080;

    public const uint NO_ERROR = 0;
    public const uint ERROR_ACCESS_DENIED = 5;
    public const uint ERROR_FILE_EXISTS = 80;
    public const uint ERROR_BUFFER_OVERFLOW = 111;
    public const uint ERROR_NOT_FOUND = 1168;
    public const uint ERROR_NO_DATA = 232;
    public const uint ERROR_OBJECT_ALREADY_EXISTS = 5010;

    public const int IP_PREFIX_ORIGIN_MANUAL = 1;
    public const int IP_PREFIX_ORIGIN_WELL_KNOWN = 2;
    public const int IP_SUFFIX_ORIGIN_MANUAL = 1;

    public const int ROUTE_PROTOCOL_NET_MGMT = 3;
    public const int ROUTE_ORIGIN_MANUAL = 0;

    public const int ROUTER_DISCOVERY_DISABLED = 0;
    public const int ROUTER_DISCOVERY_ENABLED = 1;

    public const uint IF_TYPE_ETHERNET_CSMACD = 6;
    public const uint IF_TYPE_IEEE80211 = 71;
    public const uint IF_TYPE_SOFTWARE_LOOPBACK = 24;
    public const uint IF_TYPE_TUNNEL = 131;
    public const uint IF_TYPE_WWANPP = 243;
    public const uint IF_TYPE_WWANPP2 = 244;

    public const uint IF_OPER_STATUS_UP = 1;

    public const int DAD_STATE_INVALID = 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SocketAddress
{
    public IntPtr lpSockaddr;
    public int iSockaddrLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IpAdapterAddresses
{
    public uint Length;
    public IntPtr Next;
    public IntPtr AdapterName;
    public IntPtr FirstUnicastAddress;
    public IntPtr FirstAnycastAddress;
    public IntPtr FirstMulticastAddress;
    public IntPtr FirstDnsServerAddress;
    public IntPtr DnsSuffix;
    public IntPtr Description;
    public IntPtr FriendlyName;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public byte[] PhysicalAddress;

    public uint PhysicalAddressLength;
    public uint Flags;
    public uint Mtu;
    public uint IfType;
    public uint OperStatus;
    public uint Ipv6IfIndex;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public uint[] ZoneIndices;

    public IntPtr FirstPrefix;
    public ulong TransmitLinkSpeed;
    public ulong ReceiveLinkSpeed;
    public IntPtr FirstWinsServerAddress;
    public IntPtr FirstGatewayAddress;
    public uint Ipv4Metric;
    public uint Ipv6Metric;
    public ulong Luid;
}

internal static class IpAdapterFlags
{
    public const uint Dhcpv4Enabled = 1u << 2;
    public const uint Ipv4Enabled = 1u << 7;
    public const uint Ipv6Enabled = 1u << 8;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IpAdapterUnicastAddress
{
    public uint Length;
    public uint Flags;
    public IntPtr Next;
    public SocketAddress Address;
    public int PrefixOrigin;
    public int SuffixOrigin;
    public int DadState;
    public uint ValidLifetime;
    public uint PreferredLifetime;
    public uint LeaseLifetime;
    public byte OnLinkPrefixLength;
}

/// <summary>IP_ADAPTER_DNS_SERVER_ADDRESS and IP_ADAPTER_GATEWAY_ADDRESS share this layout.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IpAdapterAddressEntry
{
    public uint Length;
    public uint Reserved;
    public IntPtr Next;
    public SocketAddress Address;
}

/// <summary>NL_INTERFACE_OFFLOAD_ROD packs eight bits into one byte.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MibIpInterfaceOffload
{
    public byte Flags;
}

/// <summary>
/// MIB_IPINTERFACE_ROW. Field layout matches the Windows SDK; the trailing <c>Size = 176</c>
/// keeps the marshal buffer slightly larger than the native row (164 bytes) so the
/// read-modify-write call can never overrun it.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 176)]
internal struct MibIpInterfaceRow
{
    public ushort Family;
    public ulong InterfaceLuid;
    public uint InterfaceIndex;
    public uint MaxReassemblySize;
    public ulong InterfaceIdentifier;
    public uint MinRouterAdvertisementInterval;
    public uint MaxRouterAdvertisementInterval;
    public byte AdvertisingEnabled;
    public byte ForwardingEnabled;
    public byte WeakHostSend;
    public byte WeakHostReceive;
    public byte UseAutomaticMetric;
    public byte UseNeighborUnreachabilityDetection;
    public byte ManagedAddressConfigurationSupported;
    public byte OtherStatefulConfigurationSupported;
    public byte AdvertiseDefaultRoute;
    public int RouterDiscoveryBehavior;
    public uint DadTransmits;
    public uint BaseReachableTime;
    public uint RetransmitTime;
    public uint PathMtuDiscoveryTimeout;
    public int LinkLocalAddressBehavior;
    public uint LinkLocalAddressTimeout;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public uint[] ZoneIndices;

    public uint SitePrefixLength;
    public uint Metric;
    public uint NlMtu;
    public byte Connected;
    public byte SupportsWakeUpPatterns;
    public byte SupportsNeighborDiscovery;
    public byte SupportsRouterDiscovery;
    public uint ReachableTime;
    public MibIpInterfaceOffload TransmitOffload;
    public MibIpInterfaceOffload ReceiveOffload;
    public byte DisableDefaultRoutes;

    public static MibIpInterfaceRow Create(ushort family, ulong luid, uint interfaceIndex) => new()
    {
        Family = family,
        InterfaceLuid = luid,
        InterfaceIndex = interfaceIndex,
        ZoneIndices = new uint[16]
    };
}

internal static class SockaddrReader
{
    public static IPAddress? ReadAddress(IntPtr sockaddr)
    {
        if (sockaddr == IntPtr.Zero)
        {
            return null;
        }

        var family = (ushort)Marshal.ReadInt16(sockaddr, 0);
        switch (family)
        {
            case NativeConstants.AF_INET:
            {
                var bytes = new byte[4];
                Marshal.Copy(IntPtr.Add(sockaddr, 4), bytes, 0, 4);
                return new IPAddress(bytes);
            }

            case NativeConstants.AF_INET6:
            {
                var bytes = new byte[16];
                Marshal.Copy(IntPtr.Add(sockaddr, 8), bytes, 0, 16);
                var scopeId = (uint)Marshal.ReadInt32(sockaddr, 24);
                return new IPAddress(bytes, scopeId);
            }

            default:
                return null;
        }
    }

    public static string? ReadString(IntPtr value, bool unicode)
    {
        if (value == IntPtr.Zero)
        {
            return null;
        }

        return unicode ? Marshal.PtrToStringUni(value) : Marshal.PtrToStringAnsi(value);
    }
}

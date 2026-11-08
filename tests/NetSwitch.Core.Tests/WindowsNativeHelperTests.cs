using System.Net;
using System.Runtime.InteropServices;
using NetSwitch.Platform.Windows;
using NetSwitch.Platform.Windows.Native;
using Xunit;

namespace NetSwitch.Core.Tests;

/// <summary>
/// Pure marshaling checks that can run on Linux. They pin down the struct offsets and
/// byte order that the P/Invoke code relies on, which is where a Windows backend usually
/// breaks in ways unit tests would otherwise miss until run on real hardware.
/// </summary>
public sealed class WindowsNativeHelperTests
{
    [Theory]
    [InlineData(0, "0.0.0.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(16, "255.255.0.0")]
    [InlineData(24, "255.255.255.0")]
    [InlineData(26, "255.255.255.192")]
    [InlineData(32, "255.255.255.255")]
    [InlineData(64, "255.255.255.255")]
    [InlineData(-1, "0.0.0.0")]
    public void PrefixLengthConvertsToSubnetMask(int prefix, string expected)
    {
        Assert.Equal(expected, Ipv4Mask.FromPrefixLength(prefix));
    }

    [Fact]
    public void SockaddrReaderReadsIpv4InNetworkOrder()
    {
        var buffer = Marshal.AllocHGlobal(16);
        try
        {
            for (var i = 0; i < 16; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            Marshal.WriteInt16(buffer, 0, NativeConstants.AF_INET);
            Marshal.Copy(new byte[] { 192, 168, 2, 100 }, 0, IntPtr.Add(buffer, 4), 4);

            var address = SockaddrReader.ReadAddress(buffer);

            Assert.NotNull(address);
            Assert.Equal(IPAddress.Parse("192.168.2.100"), address);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void SockaddrReaderReadsIpv6WithScopeId()
    {
        var buffer = Marshal.AllocHGlobal(28);
        try
        {
            for (var i = 0; i < 28; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            Marshal.WriteInt16(buffer, 0, NativeConstants.AF_INET6);
            var bytes = new byte[16];
            bytes[0] = 0xFE;
            bytes[1] = 0x80;
            bytes[15] = 0x01;
            Marshal.Copy(bytes, 0, IntPtr.Add(buffer, 8), 16);
            Marshal.WriteInt32(buffer, 24, 3);

            var address = SockaddrReader.ReadAddress(buffer);

            Assert.NotNull(address);
            Assert.Equal(bytes, address!.GetAddressBytes());
            Assert.Equal(3, address.ScopeId);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void IpInterfaceRowOffsetsMatchTheWindowsSdk()
    {
        // These offsets are load-bearing: the metric is read and written through them.
        Assert.Equal(44, Marshal.OffsetOf<MibIpInterfaceRow>(nameof(MibIpInterfaceRow.UseAutomaticMetric)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<MibIpInterfaceRow>(nameof(MibIpInterfaceRow.ZoneIndices)).ToInt32());
        Assert.Equal(148, Marshal.OffsetOf<MibIpInterfaceRow>(nameof(MibIpInterfaceRow.Metric)).ToInt32());

        // The native row is 168 bytes; the managed buffer is deliberately larger.
        Assert.True(Marshal.SizeOf<MibIpInterfaceRow>() >= 168);
    }

    [Fact]
    public void SockaddrInetRoundTripsIpv4AndIpv6()
    {
        Assert.Equal(28, Marshal.SizeOf<Ipv6NativeApi.SockaddrInet>());

        var v4Bytes = new byte[] { 192, 168, 2, 100 };
        var v4 = Ipv6NativeApi.SockaddrInet.FromAddress(new IPAddress(v4Bytes));
        Assert.Equal(NativeConstants.AF_INET, (int)v4.Family);
        Assert.Equal(new IPAddress(v4Bytes), v4.ToAddress());

        var v6Bytes = new byte[16];
        v6Bytes[0] = 0xFE;
        v6Bytes[1] = 0x80;
        v6Bytes[15] = 0x01;
        var v6Address = new IPAddress(v6Bytes, 7);
        var v6 = Ipv6NativeApi.SockaddrInet.FromAddress(v6Address);
        Assert.Equal(NativeConstants.AF_INET6, (int)v6.Family);
        Assert.Equal(v6Address, v6.ToAddress());
        Assert.Equal(7, v6.ToAddress()!.ScopeId);
    }

    [Fact]
    public void SockaddrInetWritesBytesInNetworkOrder()
    {
        var value = Ipv6NativeApi.SockaddrInet.FromAddress(IPAddress.Parse("192.168.2.100"));
        var buffer = Marshal.AllocHGlobal(28);
        try
        {
            Marshal.StructureToPtr(value, buffer, false);

            var bytes = new byte[4];
            Marshal.Copy(IntPtr.Add(buffer, 4), bytes, 0, 4);
            Assert.Equal(new byte[] { 192, 168, 2, 100 }, bytes);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void Ipv6RowOffsetsMatchNetioapiHeader()
    {
        Assert.Equal(0, Marshal.OffsetOf<Ipv6NativeApi.MibUnicastIpAddressRow>(
            nameof(Ipv6NativeApi.MibUnicastIpAddressRow.Address)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<Ipv6NativeApi.MibUnicastIpAddressRow>(
            nameof(Ipv6NativeApi.MibUnicastIpAddressRow.InterfaceLuid)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<Ipv6NativeApi.MibUnicastIpAddressRow>(
            nameof(Ipv6NativeApi.MibUnicastIpAddressRow.InterfaceIndex)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<Ipv6NativeApi.MibUnicastIpAddressRow>(
            nameof(Ipv6NativeApi.MibUnicastIpAddressRow.PrefixOrigin)).ToInt32());
        Assert.Equal(60, Marshal.OffsetOf<Ipv6NativeApi.MibUnicastIpAddressRow>(
            nameof(Ipv6NativeApi.MibUnicastIpAddressRow.OnLinkPrefixLength)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<Ipv6NativeApi.MibUnicastIpAddressRow>(
            nameof(Ipv6NativeApi.MibUnicastIpAddressRow.DadState)).ToInt32());
        Assert.Equal(80, Marshal.SizeOf<Ipv6NativeApi.MibUnicastIpAddressRow>());
    }

    [Fact]
    public void Ipv6ForwardRowOffsetsMatchNetioapiHeader()
    {
        Assert.Equal(12, Marshal.OffsetOf<Ipv6NativeApi.MibIpForwardRow2>(
            nameof(Ipv6NativeApi.MibIpForwardRow2.DestinationPrefix)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<Ipv6NativeApi.MibIpForwardRow2>(
            nameof(Ipv6NativeApi.MibIpForwardRow2.NextHop)).ToInt32());
        Assert.Equal(84, Marshal.OffsetOf<Ipv6NativeApi.MibIpForwardRow2>(
            nameof(Ipv6NativeApi.MibIpForwardRow2.Metric)).ToInt32());
        Assert.Equal(88, Marshal.OffsetOf<Ipv6NativeApi.MibIpForwardRow2>(
            nameof(Ipv6NativeApi.MibIpForwardRow2.Protocol)).ToInt32());
        Assert.Equal(104, Marshal.SizeOf<Ipv6NativeApi.MibIpForwardRow2>());
    }

    [Fact]
    public void DnsInterfaceSettingsMatchesDnsSettingsHeader()
    {
        Assert.Equal(0, Marshal.OffsetOf<Ipv6NativeApi.DnsInterfaceSettings>(
            nameof(Ipv6NativeApi.DnsInterfaceSettings.Version)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<Ipv6NativeApi.DnsInterfaceSettings>(
            nameof(Ipv6NativeApi.DnsInterfaceSettings.Flags)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<Ipv6NativeApi.DnsInterfaceSettings>(
            nameof(Ipv6NativeApi.DnsInterfaceSettings.NameServer)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<Ipv6NativeApi.DnsInterfaceSettings>(
            nameof(Ipv6NativeApi.DnsInterfaceSettings.ProfileNameServer)).ToInt32());
        Assert.Equal(64, Marshal.SizeOf<Ipv6NativeApi.DnsInterfaceSettings>());
    }

    [Fact]
    public void NetCfgBindNameMatchingAcceptsEitherGuidFormat()
    {
        var guid = Guid.Parse("0f2a7b4c-1d3e-4f5a-8b9c-0d1e2f3a4b5c");

        Assert.True(NetCfgApi.BindNameMatches($@"\DEVICE\{{{guid:D}}}", guid));
        Assert.True(NetCfgApi.BindNameMatches($@"\DEVICE\{{{guid}}}", guid));
        Assert.False(NetCfgApi.BindNameMatches(
            @"\DEVICE\{11111111-2222-3333-4444-555555555555}", guid));
        Assert.False(NetCfgApi.BindNameMatches(null, guid));
        Assert.False(NetCfgApi.BindNameMatches("ms_tcpip6", guid));
    }

    [Fact]
    public void VirtualAdapterDetectionUsesDescriptionAndType()
    {
        Assert.True(VirtualAdapterDetector.IsVirtual(
            "VMware Virtual Ethernet Adapter for VMnet8", "VMware Network Adapter VMnet8", 6));
        Assert.True(VirtualAdapterDetector.IsVirtual(
            "Hyper-V Virtual Ethernet Adapter", "vEthernet (Default Switch)", 6));
        Assert.True(VirtualAdapterDetector.IsVirtual("anything", "anything", 131));
        Assert.False(VirtualAdapterDetector.IsVirtual(
            "Intel(R) Wi-Fi 6 AX201 160MHz", "WLAN", 71));
        Assert.False(VirtualAdapterDetector.IsVirtual(
            "Realtek PCIe GbE Family Controller", "以太网", 6));
    }
}

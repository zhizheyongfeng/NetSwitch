using System.Runtime.InteropServices;
using System.Text;
using NetSwitch.Platform.Windows.Native;
using Xunit;

namespace NetSwitch.Core.Tests;

/// <summary>
/// Native layout checks for wlanapi.h. A wrong size shifts every entry after the first,
/// which would silently corrupt profile names and connection state on real hardware.
/// </summary>
public sealed class WlanInteropTests
{
    [Fact]
    public void WlanInterfaceInfoLayoutMatchesHeader()
    {
        Assert.Equal(532, Marshal.SizeOf<WlanNativeApi.WlanInterfaceInfo>());
    }

    [Fact]
    public void WlanProfileInfoLayoutMatchesHeader()
    {
        Assert.Equal(516, Marshal.SizeOf<WlanNativeApi.WlanProfileInfo>());
    }

    [Fact]
    public void ListHeadersAreTwoDwords()
    {
        Assert.Equal(8, Marshal.SizeOf<WlanNativeApi.WlanInterfaceInfoList>());
        Assert.Equal(8, Marshal.SizeOf<WlanNativeApi.WlanProfileInfoList>());
        Assert.Equal(8, 2 * sizeof(uint));
    }

    [Fact]
    public void Dot11SsidLayoutMatchesHeader()
    {
        Assert.Equal(36, Marshal.SizeOf<WlanNativeApi.Dot11Ssid>());
    }

    [Fact]
    public void AssociationAttributesLayoutMatchesHeader()
    {
        Assert.Equal(68, Marshal.SizeOf<WlanNativeApi.WlanAssociationAttributes>());
    }

    [Fact]
    public void ConnectionAttributesLayoutMatchesHeader()
    {
        Assert.Equal(604, Marshal.SizeOf<WlanNativeApi.WlanConnectionAttributes>());
    }

    [Fact]
    public void SecurityAttributesAreFourInts()
    {
        Assert.Equal(16, Marshal.SizeOf<WlanNativeApi.WlanSecurityAttributes>());
    }

    [Fact]
    public void ConnectionParametersLayoutMatchesPointerSize()
    {
        var expected = IntPtr.Size == 8 ? 40 : 24;
        Assert.Equal(expected, Marshal.SizeOf<WlanNativeApi.WlanConnectionParameters>());
    }

    [Fact]
    public void DecodeSsidUsesDeclaredLengthAndTrimsNulls()
    {
        var bytes = new byte[32];
        var text = Encoding.UTF8.GetBytes("Office-5G");
        text.CopyTo(bytes, 0);
        bytes[text.Length] = 0;

        var decoded = WlanNativeApi.DecodeSsid(bytes, (uint)text.Length);

        Assert.Equal("Office-5G", decoded);
    }

    [Fact]
    public void DecodeSsidHandlesEmptyAndNull()
    {
        Assert.Equal(string.Empty, WlanNativeApi.DecodeSsid(null, 0));
        Assert.Equal(string.Empty, WlanNativeApi.DecodeSsid(new byte[32], 0));
    }

    [Fact]
    public void DecodeSsidClampsLengthToBuffer()
    {
        var bytes = Encoding.UTF8.GetBytes("abc");

        var decoded = WlanNativeApi.DecodeSsid(bytes, 100);

        Assert.Equal("abc", decoded);
    }

    [Fact]
    public void WlanErrorIncludesCodeAndWin32Message()
    {
        var error = WlanNativeApi.Error(
            WlanNativeApi.ErrorNotFound,
            "读取 Wi-Fi 失败");

        Assert.Contains("读取 Wi-Fi 失败", error.Message);
        Assert.Contains(WlanNativeApi.ErrorNotFound.ToString(), error.Message);
    }
}

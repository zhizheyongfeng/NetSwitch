using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NetSwitch.Platform.Windows.Native;

/// <summary>
/// WLAN API (wlanapi.dll) interop used to list saved profiles and connect/disconnect the
/// current user's saved Wi-Fi profiles. Passwords are never read: Windows keeps them in
/// the profile store and <c>WlanConnect</c> only takes the profile name.
///
/// All Wlan* calls are thread-safe; the handle is opened per operation by
/// <see cref="WlanClient"/>. String fields are fixed 256-character arrays per wlanapi.h.
/// </summary>
internal static class WlanNativeApi
{
    public const uint ClientVersion = 2;
    public const uint WlanMaxNameLength = 256;
    public const int WlanConnectionModeProfile = 0;
    public const int Dot11BssTypeInfrastructure = 1;
    public const int WlanInterfaceStateConnected = 1;
    public const int WlanInterfaceStateDisconnected = 4;
    public const int WlanIntfOpcodeInterfaceState = 6;
    public const int WlanIntfOpcodeCurrentConnection = 7;

    public const uint ErrorSuccess = 0;
    public const uint ErrorInvalidHandle = 6;
    public const uint ErrorNotFound = 1168;
    public const uint ErrorServiceNotActive = 1062;
    public const uint ErrorInvalidState = 5023;

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanOpenHandle(
        uint clientVersion,
        IntPtr reserved,
        out uint negotiatedVersion,
        out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanEnumInterfaces(
        IntPtr clientHandle,
        IntPtr reserved,
        out IntPtr interfaceList);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    internal static extern uint WlanGetProfileList(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        IntPtr reserved,
        out IntPtr profileList);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    internal static extern uint WlanConnect(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        ref WlanConnectionParameters parameters,
        IntPtr reserved);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanDisconnect(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        IntPtr reserved);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanQueryInterface(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        int opcode,
        IntPtr reserved,
        out uint dataSize,
        out IntPtr data,
        out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    internal static extern void WlanFreeMemory(IntPtr memory);

    public static Exception Error(uint code, string message)
    {
        var detail = new Win32Exception((int)code).Message;
        return new InvalidOperationException($"{message}：{detail}（错误 {code}）");
    }

    /// <summary>
    /// SSIDs are byte strings, not necessarily UTF-8. UTF-8 covers the common case and
    /// replacement characters are preferable to failing the whole operation.
    /// </summary>
    internal static string DecodeSsid(byte[]? bytes, uint length)
    {
        if (bytes is null || length == 0)
        {
            return string.Empty;
        }

        var count = (int)Math.Min(length, (uint)bytes.Length);
        return Encoding.UTF8.GetString(bytes, 0, count).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = (int)WlanMaxNameLength)]
        public string InterfaceDescription;

        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanInterfaceInfoList
    {
        public uint NumberOfItems;
        public uint Index;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanProfileInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = (int)WlanMaxNameLength)]
        public string ProfileName;

        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanProfileInfoList
    {
        public uint NumberOfItems;
        public uint Index;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Dot11Ssid
    {
        public uint Length;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Ssid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanAssociationAttributes
    {
        public Dot11Ssid Ssid;
        public int BssType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Bssid;

        public int PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint RxRate;
        public uint TxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanSecurityAttributes
    {
        public int SecurityEnabled;
        public int OneXEnabled;
        public int AuthAlgorithm;
        public int CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanConnectionAttributes
    {
        public int State;
        public int ConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = (int)WlanMaxNameLength)]
        public string ProfileName;

        public WlanAssociationAttributes Association;
        public WlanSecurityAttributes Security;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanConnectionParameters
    {
        public int ConnectionMode;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Profile;

        public IntPtr Dot11Ssid;
        public IntPtr DesiredBssidList;
        public int BssType;
        public uint Flags;
    }
}

/// <summary>
/// One WLAN client handle. WLAN calls are independent of the IP Helper adapter snapshot;
/// the interface is identified by the adapter GUID (the same value used as StableId).
/// </summary>
internal sealed class WlanClient : IDisposable
{
    private IntPtr _handle;

    public WlanClient()
    {
        var result = Open(out var handle);
        if (result != WlanNativeApi.ErrorSuccess)
        {
            throw WlanNativeApi.Error(result, "打开 WLAN 服务失败（WLAN AutoConfig 可能未运行）");
        }

        _handle = handle;
    }

    public IReadOnlyList<WlanNativeApi.WlanInterfaceInfo> GetInterfaces()
    {
        var result = WlanNativeApi.WlanEnumInterfaces(_handle, IntPtr.Zero, out var list);
        if (result != WlanNativeApi.ErrorSuccess)
        {
            throw WlanNativeApi.Error(result, "枚举 Wi-Fi 接口失败");
        }

        try
        {
            var count = unchecked((uint)Marshal.ReadInt32(list));
            var size = Marshal.SizeOf<WlanNativeApi.WlanInterfaceInfo>();
            var interfaces = new List<WlanNativeApi.WlanInterfaceInfo>((int)Math.Min(count, 32));
            for (var i = 0u; i < count; i++)
            {
                var entry = IntPtr.Add(list, 8 + (int)(i * (uint)size));
                interfaces.Add(Marshal.PtrToStructure<WlanNativeApi.WlanInterfaceInfo>(entry));
            }

            return interfaces;
        }
        finally
        {
            WlanNativeApi.WlanFreeMemory(list);
        }
    }

    public Guid RequireInterface(string stableId)
    {
        if (!Guid.TryParse(stableId, out var guid))
        {
            throw new ArgumentException($"适配器标识不是 GUID：{stableId}", nameof(stableId));
        }

        return GetInterfaces().Any(info => info.InterfaceGuid == guid)
            ? guid
            : throw new InvalidOperationException($"找不到 Wi-Fi 接口 {guid:B}。");
    }

    public IReadOnlyList<string> GetProfileNames(Guid interfaceGuid)
    {
        var guid = interfaceGuid;
        var result = WlanNativeApi.WlanGetProfileList(_handle, ref guid, IntPtr.Zero, out var list);
        if (result == WlanNativeApi.ErrorNotFound)
        {
            return Array.Empty<string>();
        }

        if (result != WlanNativeApi.ErrorSuccess)
        {
            throw WlanNativeApi.Error(result, "读取已保存的 Wi-Fi Profile 失败");
        }

        try
        {
            var count = unchecked((uint)Marshal.ReadInt32(list));
            var size = Marshal.SizeOf<WlanNativeApi.WlanProfileInfo>();
            var names = new List<string>((int)Math.Min(count, 128));
            for (var i = 0u; i < count; i++)
            {
                var entry = IntPtr.Add(list, 8 + (int)(i * (uint)size));
                var profile = Marshal.PtrToStructure<WlanNativeApi.WlanProfileInfo>(entry);
                if (!string.IsNullOrWhiteSpace(profile.ProfileName))
                {
                    names.Add(profile.ProfileName);
                }
            }

            return names;
        }
        finally
        {
            WlanNativeApi.WlanFreeMemory(list);
        }
    }

    public WlanNativeApi.WlanConnectionAttributes? GetConnection(Guid interfaceGuid)
    {
        var guid = interfaceGuid;
        var result = WlanNativeApi.WlanQueryInterface(
            _handle,
            ref guid,
            WlanNativeApi.WlanIntfOpcodeCurrentConnection,
            IntPtr.Zero,
            out var size,
            out var data,
            out _);

        if (result == WlanNativeApi.ErrorNotFound ||
            result == WlanNativeApi.ErrorInvalidState ||
            result == WlanNativeApi.ErrorServiceNotActive)
        {
            return null;
        }

        if (result != WlanNativeApi.ErrorSuccess)
        {
            throw WlanNativeApi.Error(result, "读取当前 Wi-Fi 连接失败");
        }

        try
        {
            if (data == IntPtr.Zero || size < (uint)Marshal.SizeOf<WlanNativeApi.WlanConnectionAttributes>())
            {
                return null;
            }

            return Marshal.PtrToStructure<WlanNativeApi.WlanConnectionAttributes>(data);
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                WlanNativeApi.WlanFreeMemory(data);
            }
        }
    }

    public void Connect(Guid interfaceGuid, string profileName)
    {
        var guid = interfaceGuid;
        var parameters = new WlanNativeApi.WlanConnectionParameters
        {
            ConnectionMode = WlanNativeApi.WlanConnectionModeProfile,
            Profile = profileName,
            Dot11Ssid = IntPtr.Zero,
            DesiredBssidList = IntPtr.Zero,
            BssType = WlanNativeApi.Dot11BssTypeInfrastructure,
            Flags = 0
        };

        var result = WlanNativeApi.WlanConnect(_handle, ref guid, ref parameters, IntPtr.Zero);
        if (result != WlanNativeApi.ErrorSuccess)
        {
            throw WlanNativeApi.Error(result, $"连接 Wi-Fi Profile「{profileName}」失败");
        }
    }

    public void Disconnect(Guid interfaceGuid)
    {
        var guid = interfaceGuid;
        var result = WlanNativeApi.WlanDisconnect(_handle, ref guid, IntPtr.Zero);
        if (result is WlanNativeApi.ErrorSuccess or WlanNativeApi.ErrorInvalidState)
        {
            return;
        }

        throw WlanNativeApi.Error(result, "断开 Wi-Fi 失败");
    }

    public void Dispose()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            WlanNativeApi.WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static uint Open(out IntPtr handle) =>
        WlanNativeApi.WlanOpenHandle(WlanNativeApi.ClientVersion, IntPtr.Zero, out _, out handle);
}

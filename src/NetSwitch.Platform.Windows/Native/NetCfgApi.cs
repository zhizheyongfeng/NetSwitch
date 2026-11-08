using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NetSwitch.Platform.Windows.Native;

/// <summary>
/// Minimal INetCfg (netcfgx.dll) interop used to toggle the ms_tcpip6 binding on one
/// adapter. The COM interface layouts below are taken from netcfgx.h; the declaration
/// order is the vtable order, so it must not be rearranged.
///
/// All calls run on a dedicated STA thread and the write lock is released before the
/// object is uninitialized. Public entry points are Windows-only.
/// </summary>
internal static class NetCfgApi
{
    private static readonly Guid ClsidCNetCfg = new("5b035261-40f9-11d1-aaec-00805fc1270e");
    private static readonly Guid IidINetCfg = new("c0e8ae93-306e-11d1-aacf-00805fc1270e");
    private static readonly Guid DevClassNet = new("4d36e972-e325-11ce-bfc1-08002be10318");

    private const uint ClsctxInprocServer = 0x1;
    private const uint CoinitApartmentThreaded = 0x2;
    private const uint EbpBelow = 0x2;
    private const uint WriteLockTimeoutMilliseconds = 5000;
    private const int NetCfgENoWriteLock = unchecked((int)0x8004A024);
    private const int NetCfgEAlreadyInitialized = unchecked((int)0x8004A020);

    public static Task SetIpv6BindingAsync(string adapterStableId, bool enable)
    {
        if (!Guid.TryParse(adapterStableId, out var adapterGuid))
        {
            throw new ArgumentException($"适配器标识不是 GUID：{adapterStableId}", nameof(adapterStableId));
        }

        return RunStaAsync(() => SetIpv6Binding(adapterGuid, enable));
    }

    internal static bool BindNameMatches(string? bindName, Guid adapterGuid)
    {
        if (string.IsNullOrWhiteSpace(bindName))
        {
            return false;
        }

        // Adapter bind names are usually "\DEVICE\{GUID}"; compare the GUID text inside.
        return bindName.Contains(adapterGuid.ToString("B"), StringComparison.OrdinalIgnoreCase) ||
               bindName.Contains(adapterGuid.ToString("D"), StringComparison.OrdinalIgnoreCase);
    }

    private static void SetIpv6Binding(Guid adapterGuid, bool enable)
    {
        INetCfg? cfg = null;
        INetCfgLock? cfgLock = null;
        var initialized = false;
        var writeLockAcquired = false;

        try
        {
            var clsid = ClsidCNetCfg;
            var iid = IidINetCfg;
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out var unknown),
                "创建网络配置对象失败");
            try
            {
                cfg = (INetCfg)Marshal.GetObjectForIUnknown(unknown);
            }
            finally
            {
                Marshal.Release(unknown);
            }

            var initResult = cfg.Initialize(IntPtr.Zero);
            if (initResult == NetCfgEAlreadyInitialized)
            {
                throw new InvalidOperationException("网络配置接口已被其他组件初始化。");
            }

            Check(initResult, "初始化网络配置接口失败");
            initialized = true;

            cfgLock = (INetCfgLock)cfg;
            var lockResult = cfgLock.AcquireWriteLock(WriteLockTimeoutMilliseconds, "NetSwitch", out var lockHolder);
            if (lockResult == NetCfgENoWriteLock)
            {
                throw new InvalidOperationException(
                    "其他程序正在修改网络配置，暂时无法获取写锁。请关闭 Windows 网络设置等窗口后重试。");
            }

            Check(lockResult, "获取网络配置写锁失败");
            writeLockAcquired = true;
            if (lockHolder != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(lockHolder);
            }

            var netClass = DevClassNet;
            Check(cfg.EnumComponents(ref netClass, out var components), "枚举网络适配器组件失败");
            if (components is null)
            {
                throw new InvalidOperationException("枚举网络适配器组件时没有返回结果。");
            }

            INetCfgComponent? adapter = null;
            try
            {
                while (components.Next(1, out var component, out var fetched) == 0 && fetched == 1)
                {
                    if (component is null)
                    {
                        break;
                    }

                    if (MatchesAdapter(component, adapterGuid))
                    {
                        adapter = component;
                        break;
                    }

                    Release(component);
                }
            }
            finally
            {
                Release(components);
            }

            if (adapter is null)
            {
                throw new InvalidOperationException($"在网络配置组件中找不到适配器 {adapterGuid:B}。");
            }

            try
            {
                Check(cfg.FindComponent("ms_tcpip6", out var tcpip6), "找不到 IPv6 协议组件（ms_tcpip6）");
                if (tcpip6 is null)
                {
                    throw new InvalidOperationException("IPv6 协议组件（ms_tcpip6）不可用。");
                }

                try
                {
                    var bindings = (INetCfgComponentBindings)tcpip6;
                    Check(bindings.EnumBindingPaths(EbpBelow, out var paths), "枚举 IPv6 绑定路径失败");
                    if (paths is null)
                    {
                        throw new InvalidOperationException("枚举 IPv6 绑定路径时没有返回结果。");
                    }

                    var matched = 0;
                    try
                    {
                        while (paths.Next(1, out var path, out var fetched) == 0 && fetched == 1)
                        {
                            if (path is null)
                            {
                                break;
                            }

                            try
                            {
                                if (PathTargetsAdapter(path, adapterGuid))
                                {
                                    Check(
                                        path.Enable(enable ? 1 : 0),
                                        enable ? "启用 IPv6 绑定失败" : "禁用 IPv6 绑定失败");
                                    matched++;
                                }
                            }
                            finally
                            {
                                Release(path);
                            }
                        }
                    }
                    finally
                    {
                        Release(paths);
                    }

                    if (matched == 0)
                    {
                        throw new InvalidOperationException(
                            "没有找到该适配器的 IPv6 绑定路径，IPv6 绑定状态未修改。");
                    }
                }
                finally
                {
                    Release(tcpip6);
                }
            }
            finally
            {
                Release(adapter);
            }

            Check(cfg.Apply(), "提交 IPv6 绑定修改失败");
        }
        finally
        {
            if (writeLockAcquired && cfgLock is not null)
            {
                try
                {
                    cfgLock.ReleaseWriteLock();
                }
                catch (Exception)
                {
                    // Uninitialize below still releases the component references.
                }
            }

            if (cfg is not null)
            {
                if (initialized)
                {
                    try
                    {
                        cfg.Uninitialize();
                    }
                    catch (Exception)
                    {
                        // Best effort; the process is about to leave the COM object behind.
                    }
                }

                Release(cfg);
            }
        }
    }

    private static bool PathTargetsAdapter(INetCfgBindingPath path, Guid adapterGuid)
    {
        if (path.EnumBindingInterfaces(out var interfaces) < 0 || interfaces is null)
        {
            return false;
        }

        try
        {
            while (interfaces.Next(1, out var bindingInterface, out var fetched) == 0 && fetched == 1)
            {
                if (bindingInterface is null)
                {
                    break;
                }

                try
                {
                    if (bindingInterface.GetLowerComponent(out var lower) >= 0 && lower is not null)
                    {
                        try
                        {
                            if (MatchesAdapter(lower, adapterGuid))
                            {
                                return true;
                            }
                        }
                        finally
                        {
                            Release(lower);
                        }
                    }
                }
                finally
                {
                    Release(bindingInterface);
                }
            }
        }
        finally
        {
            Release(interfaces);
        }

        return false;
    }

    private static bool MatchesAdapter(INetCfgComponent component, Guid adapterGuid)
    {
        if (component.GetInstanceGuid(out var instance) >= 0 && instance == adapterGuid)
        {
            return true;
        }

        if (component.GetBindName(out var bindNamePointer) < 0 || bindNamePointer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return BindNameMatches(Marshal.PtrToStringUni(bindNamePointer), adapterGuid);
        }
        finally
        {
            Marshal.FreeCoTaskMem(bindNamePointer);
        }
    }

    private static Task RunStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var comResult = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
            try
            {
                if (comResult < 0)
                {
                    throw new COMException("在当前线程初始化 COM 失败。", comResult);
                }

                action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                if (comResult >= 0)
                {
                    CoUninitialize();
                }
            }
        })
        {
            IsBackground = true,
            Name = "NetSwitch INetCfg"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Check(int hr, string message)
    {
        if (hr >= 0)
        {
            return;
        }

        var detail = Marshal.GetExceptionForHR(hr)?.Message ?? new Win32Exception(hr).Message;
        throw new InvalidOperationException($"{message}：{detail}（HRESULT 0x{hr:X8}）");
    }

    private static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(value);
        }
        catch (ArgumentException)
        {
            // The object was already released; nothing else to do.
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [ComImport]
    [Guid("c0e8ae93-306e-11d1-aacf-00805fc1270e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetCfg
    {
        [PreserveSig]
        int Initialize(IntPtr reserved);

        [PreserveSig]
        int Uninitialize();

        [PreserveSig]
        int Apply();

        [PreserveSig]
        int Cancel();

        [PreserveSig]
        int EnumComponents(ref Guid classGuid, out IEnumNetCfgComponent? enumComponent);

        [PreserveSig]
        int FindComponent(
            [MarshalAs(UnmanagedType.LPWStr)] string infId,
            out INetCfgComponent? component);

        [PreserveSig]
        int QueryNetCfgClass(ref Guid classGuid, ref Guid riid, out IntPtr ppv);
    }

    [ComImport]
    [Guid("c0e8ae99-306e-11d1-aacf-00805fc1270e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetCfgComponent
    {
        [PreserveSig]
        int GetDisplayName(out IntPtr displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName);

        [PreserveSig]
        int GetHelpText(out IntPtr helpText);

        [PreserveSig]
        int GetId(out IntPtr id);

        [PreserveSig]
        int GetCharacteristics(out uint characteristics);

        [PreserveSig]
        int GetInstanceGuid(out Guid instanceGuid);

        [PreserveSig]
        int GetPnpDevNodeId(out IntPtr devNodeId);

        [PreserveSig]
        int GetClassGuid(out Guid classGuid);

        [PreserveSig]
        int GetBindName(out IntPtr bindName);

        [PreserveSig]
        int GetDeviceStatus(out uint status);

        [PreserveSig]
        int OpenParamKey(out IntPtr key);

        [PreserveSig]
        int RaisePropertyUi(IntPtr parentWindow, uint flags, IntPtr context);
    }

    [ComImport]
    [Guid("c0e8ae92-306e-11d1-aacf-00805fc1270e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumNetCfgComponent
    {
        [PreserveSig]
        int Next(uint count, out INetCfgComponent? component, out uint fetched);

        [PreserveSig]
        int Skip(uint count);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IEnumNetCfgComponent? enumComponent);
    }

    [ComImport]
    [Guid("c0e8ae9e-306e-11d1-aacf-00805fc1270e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetCfgComponentBindings
    {
        [PreserveSig]
        int BindTo(INetCfgComponent component);

        [PreserveSig]
        int UnbindFrom(INetCfgComponent component);

        [PreserveSig]
        int SupportsBindingInterface(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string interfaceName);

        [PreserveSig]
        int IsBoundTo(INetCfgComponent component);

        [PreserveSig]
        int IsBindableTo(INetCfgComponent component);

        [PreserveSig]
        int EnumBindingPaths(uint flags, out IEnumNetCfgBindingPath? enumPath);

        [PreserveSig]
        int MoveBefore(INetCfgBindingPath source, INetCfgBindingPath destination);

        [PreserveSig]
        int MoveAfter(INetCfgBindingPath source, INetCfgBindingPath destination);
    }

    [ComImport]
    [Guid("c0e8ae9f-306e-11d1-aacf-00805fc1270e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetCfgLock
    {
        [PreserveSig]
        int AcquireWriteLock(
            uint timeoutMilliseconds,
            [MarshalAs(UnmanagedType.LPWStr)] string clientDescription,
            out IntPtr currentClientDescription);

        [PreserveSig]
        int ReleaseWriteLock();

        [PreserveSig]
        int IsWriteLocked(out IntPtr currentClientDescription);
    }

    [ComImport]
    [Guid("c0e8ae91-306e-11d1-aacf-00805fc1270e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumNetCfgBindingPath
    {
        [PreserveSig]
        int Next(uint count, out INetCfgBindingPath? path, out uint fetched);

        [PreserveSig]
        int Skip(uint count);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IEnumNetCfgBindingPath? enumPath);
    }

    [ComImport]
    [Guid("c0e8ae96-306e-11d1-aacf-00805fc1270e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetCfgBindingPath
    {
        [PreserveSig]
        int IsSamePathAs(INetCfgBindingPath path);

        [PreserveSig]
        int IsSubPathOf(INetCfgBindingPath path);

        [PreserveSig]
        int IsEnabled();

        [PreserveSig]
        int Enable(int enable);

        [PreserveSig]
        int GetPathToken(out IntPtr pathToken);

        [PreserveSig]
        int GetOwner(out INetCfgComponent? owner);

        [PreserveSig]
        int GetDepth(out uint depth);

        [PreserveSig]
        int EnumBindingInterfaces(out IEnumNetCfgBindingInterface? enumInterface);
    }

    [ComImport]
    [Guid("c0e8ae90-306e-11d1-aacf-00805fc1270e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumNetCfgBindingInterface
    {
        [PreserveSig]
        int Next(uint count, out INetCfgBindingInterface? bindingInterface, out uint fetched);

        [PreserveSig]
        int Skip(uint count);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IEnumNetCfgBindingInterface? enumInterface);
    }

    [ComImport]
    [Guid("c0e8ae94-306e-11d1-aacf-00805fc1270e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetCfgBindingInterface
    {
        [PreserveSig]
        int GetName(out IntPtr interfaceName);

        [PreserveSig]
        int GetUpperComponent(out INetCfgComponent? component);

        [PreserveSig]
        int GetLowerComponent(out INetCfgComponent? component);
    }
}

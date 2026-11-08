using System.Net;
using System.Net.Sockets;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Core.Validation;

public sealed class StaticCheckService : IStaticChecker
{
    private readonly IAdapterCatalog _catalog;
    private readonly IWirelessController _wireless;
    private readonly IPrivilegeService _privileges;
    private readonly IPlatformCapabilities _capabilities;
    private readonly IConnectivityProbe _probe;

    public StaticCheckService(
        IAdapterCatalog catalog,
        IWirelessController wireless,
        IPrivilegeService privileges,
        IPlatformCapabilities capabilities,
        IConnectivityProbe probe)
    {
        _catalog = catalog;
        _wireless = wireless;
        _privileges = privileges;
        _capabilities = capabilities;
        _probe = probe;
    }

    public async Task<StaticCheckReport> ValidateAsync(
        AdapterKey key,
        DesiredState desired,
        CancellationToken ct = default)
    {
        var checks = new List<TestCheck>(DesiredStateValidator.Validate(desired));
        var adapter = await _catalog.GetAdapterAsync(key, ct).ConfigureAwait(false);

        if (adapter is null)
        {
            checks.Add(TestCheck.Fail("适配器可用性", "找不到该适配器。它可能已被移除或禁用。"));
        }
        else
        {
            checks.Add(adapter.IsUp
                ? TestCheck.Pass("适配器可用性", $"{adapter.Key.FriendlyName} · 已连接")
                : TestCheck.Warn("适配器可用性", $"{adapter.Key.FriendlyName} 当前未连接，应用后可能无法立即联网。"));

            if (!adapter.IsPhysical)
            {
                checks.Add(TestCheck.Warn("物理适配器", "这是虚拟适配器；请确认它确实需要被修改。"));
            }
        }

        checks.Add(_privileges.IsElevated
            ? TestCheck.Pass("管理员权限", "当前进程已提权，可以修改网络配置。")
            : TestCheck.Fail("管理员权限", "需要以管理员身份运行才能修改网络配置。"));

        await CheckWirelessAsync(key, desired.Wireless, checks, ct).ConfigureAwait(false);
        CheckIpv6Capability(desired.Ipv6, checks);

        if (adapter is not null && adapter.IsUp && desired.Ipv4.Mode == Ipv4Mode.Static)
        {
            await CheckAddressConflictsAsync(desired.Ipv4.Addresses, checks, ct).ConfigureAwait(false);
            CheckGatewaySubnets(desired.Ipv4, checks);
        }
        else
        {
            checks.Add(TestCheck.Skip("目标 IP 冲突预检", "DHCP 模式或适配器未连接，跳过冲突预检。"));
            checks.Add(TestCheck.Skip("网关位于子网内", "DHCP 模式或没有静态地址。"));
        }

        await CheckOnlyConnectedAdapterAsync(key, checks, ct).ConfigureAwait(false);

        return new StaticCheckReport(DesiredStateValidator.CanApply(checks), checks);
    }

    private async Task CheckWirelessAsync(
        AdapterKey key,
        WirelessDesired? wireless,
        List<TestCheck> checks,
        CancellationToken ct)
    {
        if (wireless is null || wireless.Action == WirelessAction.Keep)
        {
            return;
        }

        if (!_capabilities.SupportsWirelessProfiles)
        {
            checks.Add(TestCheck.Fail(
                "Wi-Fi 能力",
                "当前后端尚未实现 Wi-Fi 配置文件的连接与断开。"));
            return;
        }

        try
        {
            if (wireless.Action == WirelessAction.Connect)
            {
                var profiles = await _wireless.GetSavedProfilesAsync(key, ct).ConfigureAwait(false);
                checks.Add(profiles.Contains(wireless.SavedProfileName ?? string.Empty, StringComparer.Ordinal)
                    ? TestCheck.Pass("Wi-Fi Profile", $"已保存的 Profile：{wireless.SavedProfileName}")
                    : TestCheck.Fail("Wi-Fi Profile", $"找不到已保存的 Profile：{wireless.SavedProfileName}"));
            }
            else
            {
                var state = await _wireless.GetStateAsync(key, ct).ConfigureAwait(false);
                checks.Add(state is { IsConnected: true }
                    ? TestCheck.Pass("Wi-Fi 状态", $"当前连接：{state.ActiveProfileName ?? state.Ssid}")
                    : TestCheck.Warn("Wi-Fi 状态", "当前没有连接 Wi-Fi，断开动作可能没有效果。"));
            }
        }
        catch (Exception ex)
        {
            checks.Add(TestCheck.Warn("Wi-Fi 检查", $"无法读取 Wi-Fi 状态：{ex.Message}"));
        }
    }

    private void CheckIpv6Capability(Ipv6Desired? ipv6, List<TestCheck> checks)
    {
        if (ipv6 is null || ipv6.Mode == Ipv6Mode.Keep)
        {
            return;
        }

        if (!_capabilities.SupportsIpv6BindingToggle)
        {
            checks.Add(TestCheck.Fail(
                ipv6.Mode == Ipv6Mode.Disabled ? "IPv6 绑定能力" : "IPv6 配置能力",
                "当前平台不支持修改 IPv6 绑定、地址、网关和 DNS。"));
        }
        else if (ipv6.Mode == Ipv6Mode.Disabled)
        {
            checks.Add(TestCheck.Pass("IPv6 绑定能力", "可以真正禁用该适配器上的 IPv6 绑定。"));
        }
        else
        {
            checks.Add(TestCheck.Pass("IPv6 配置能力", "可以写入 IPv6 地址、网关和 DNS。"));
        }
    }

    private async Task CheckAddressConflictsAsync(
        IReadOnlyList<Ipv4Address> addresses,
        List<TestCheck> checks,
        CancellationToken ct)
    {
        if (addresses.Count == 0)
        {
            return;
        }

        var conflicts = new List<string>();
        var unreachable = new List<string>();
        foreach (var address in addresses)
        {
            try
            {
                var result = await _probe.PingAsync(address.Address, 400, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    conflicts.Add(address.Address);
                }
                else
                {
                    unreachable.Add(address.Address);
                }
            }
            catch (Exception)
            {
                unreachable.Add(address.Address);
            }
        }

        if (conflicts.Count > 0)
        {
            checks.Add(TestCheck.Warn(
                "目标 IP 冲突预检",
                $"以下地址对探测有应答，可能已被占用：{string.Join(", ", conflicts)}"));
        }
        else if (unreachable.Count == addresses.Count)
        {
            checks.Add(TestCheck.Pass(
                "目标 IP 冲突预检",
                "探测未收到应答；这不保证地址一定空闲。"));
        }
        else
        {
            checks.Add(TestCheck.Pass("目标 IP 冲突预检", "未发现明显冲突。"));
        }
    }

    private static void CheckGatewaySubnets(Ipv4Desired ipv4, List<TestCheck> checks)
    {
        if (ipv4.Mode != Ipv4Mode.Static || ipv4.Addresses.Count == 0 || ipv4.Gateways.Count == 0)
        {
            checks.Add(TestCheck.Skip("网关位于子网内", "没有需要检查的静态网关。"));
            return;
        }

        var outside = new List<string>();
        foreach (var gateway in ipv4.Gateways)
        {
            if (!IPAddress.TryParse(gateway.Address, out var gatewayAddress) ||
                gatewayAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            var inside = ipv4.Addresses.Any(address =>
            {
                if (!IPAddress.TryParse(address.Address, out var host) ||
                    host.AddressFamily != AddressFamily.InterNetwork)
                {
                    return false;
                }

                var network = GetNetworkAddress(host, address.PrefixLength);
                return IsInSubnet(gatewayAddress, network, address.PrefixLength);
            });

            if (!inside)
            {
                outside.Add(gateway.Address);
            }
        }

        checks.Add(outside.Count == 0
            ? TestCheck.Pass("网关位于子网内", "所有默认网关都在预设的子网内。")
            : TestCheck.Warn("网关位于子网内", $"以下网关不在任何预设子网内：{string.Join(", ", outside)}"));
    }

    private async Task CheckOnlyConnectedAdapterAsync(
        AdapterKey key,
        List<TestCheck> checks,
        CancellationToken ct)
    {
        try
        {
            var adapters = await _catalog.GetAdaptersAsync(ct).ConfigureAwait(false);
            var connected = adapters.Where(a => a.IsUp).ToList();
            if (connected.Count == 1 && AppConfigKeysEqual(connected[0].Key, key))
            {
                checks.Add(TestCheck.Warn(
                    "唯一联网适配器",
                    "这是当前唯一联网的适配器，修改配置期间可能短暂断网。"));
            }
            else
            {
                checks.Add(TestCheck.Pass(
                    "联网适配器",
                    connected.Count == 0 ? "当前没有联网的适配器。" : $"当前有 {connected.Count} 个适配器在线。"));
            }
        }
        catch (Exception ex)
        {
            checks.Add(TestCheck.Warn("联网适配器", $"无法枚举适配器：{ex.Message}"));
        }
    }

    private static bool AppConfigKeysEqual(AdapterKey left, AdapterKey right) =>
        string.Equals(left.Platform, right.Platform, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.StableId, right.StableId, StringComparison.OrdinalIgnoreCase);

    private static IPAddress GetNetworkAddress(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var result = (byte[])bytes.Clone();
        var fullBytes = Math.Clamp(prefixLength / 8, 0, result.Length);
        var remainingBits = prefixLength % 8;

        for (var i = fullBytes + (remainingBits > 0 ? 1 : 0); i < result.Length; i++)
        {
            result[i] = 0;
        }

        if (remainingBits > 0 && fullBytes < result.Length)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            result[fullBytes] &= mask;
        }

        return new IPAddress(result);
    }

    private static bool IsInSubnet(IPAddress address, IPAddress network, int prefixLength)
    {
        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length)
        {
            return false;
        }

        var fullBytes = Math.Clamp(prefixLength / 8, 0, addressBytes.Length);
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
            {
                return false;
            }
        }

        if (remainingBits > 0 && fullBytes < addressBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((addressBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
            {
                return false;
            }
        }

        return true;
    }
}

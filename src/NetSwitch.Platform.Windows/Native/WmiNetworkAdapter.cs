using System.Globalization;
using System.Management;
using NetSwitch.Core.Models;

namespace NetSwitch.Platform.Windows.Native;

internal sealed class WmiOperationException : Exception
{
    public WmiOperationException(string message, uint code = 0, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>WMI method return code, or 0 when the call itself failed.</summary>
    public uint Code { get; }
}

/// <summary>
/// The only component that writes IPv4 settings. Uses the documented
/// Win32_NetworkAdapterConfiguration methods; no external process is started.
/// </summary>
internal sealed class WmiNetworkAdapter : IDisposable
{
    private readonly ManagementObjectSearcher _searcher;
    private readonly ManagementObjectCollection _collection;
    private readonly ManagementObject _config;

    private WmiNetworkAdapter(
        ManagementObjectSearcher searcher,
        ManagementObjectCollection collection,
        ManagementObject config)
    {
        _searcher = searcher;
        _collection = collection;
        _config = config;
    }

    public static WmiNetworkAdapter Open(AdapterKey key)
    {
        var query = new ObjectQuery(
            $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID='{key.StableId}'");
        var searcher = new ManagementObjectSearcher(query);
        ManagementObjectCollection collection;
        try
        {
            collection = searcher.Get();
        }
        catch (ManagementException ex)
        {
            searcher.Dispose();
            throw new WmiOperationException($"无法访问 WMI 网络配置：{ex.Message}", inner: ex);
        }

        var config = collection.Cast<ManagementObject>().FirstOrDefault();
        if (config is null)
        {
            collection.Dispose();
            searcher.Dispose();
            throw new WmiOperationException(
                $"WMI 中没有找到适配器「{key.FriendlyName}」的网络配置对象（SettingID={key.StableId}）。");
        }

        return new WmiNetworkAdapter(searcher, collection, config);
    }

    public void ConfigureIpv4(Ipv4Desired desired)
    {
        if (desired.Mode == Ipv4Mode.Static)
        {
            var addresses = desired.Addresses
                .Select(address => address.Address)
                .ToArray();
            var masks = desired.Addresses
                .Select(address => Ipv4Mask.FromPrefixLength(address.PrefixLength))
                .ToArray();

            if (addresses.Length == 0)
            {
                throw new WmiOperationException("静态 IPv4 配置没有地址，已取消写入。");
            }

            Invoke("EnableStatic", new object[] { addresses, masks }, "设置静态 IPv4 地址");
            ApplyGateways(desired.Gateways);
        }
        else
        {
            Invoke("EnableDHCP", (object[]?)null, "启用 DHCP");
        }

        ApplyDns(desired.DnsMode, desired.DnsServers);

        if (desired.DnsMode == DnsMode.Static && desired.DnsServers.Count > 0)
        {
            IpHelperApi.FlushDnsCache();
        }
    }

    public void RestoreIpv4(Ipv4State snapshot)
    {
        if (snapshot.DhcpEnabled)
        {
            Invoke("EnableDHCP", (object[]?)null, "恢复 DHCP");
        }
        else
        {
            var addresses = snapshot.Addresses.Select(address => address.Address).ToArray();
            var masks = snapshot.Addresses
                .Select(address => Ipv4Mask.FromPrefixLength(address.PrefixLength))
                .ToArray();

            if (addresses.Length == 0)
            {
                Invoke("EnableDHCP", (object[]?)null, "恢复 DHCP（原状态没有静态地址）");
            }
            else
            {
                Invoke("EnableStatic", new object[] { addresses, masks }, "恢复静态 IPv4 地址");
                ApplyGateways(snapshot.Gateways);
            }
        }

        ApplyDns(snapshot.DnsMode, snapshot.DnsServers);
        IpHelperApi.FlushDnsCache();
    }

    private void ApplyGateways(IReadOnlyList<Gateway> gateways)
    {
        if (gateways.Count == 0)
        {
            try
            {
                Invoke("SetGateways", new object[] { null!, null! }, "清除默认网关");
            }
            catch (WmiOperationException)
            {
                // Some drivers reject a null gateway array. The read-back verification
                // decides whether the gateway was actually cleared, so do not abort here.
            }

            return;
        }

        var addresses = gateways.Select(gateway => gateway.Address).ToArray();
        var metrics = gateways
            .Select(gateway => (ushort)Math.Clamp(gateway.Metric ?? 1, 1, 9999))
            .ToArray();

        Invoke("SetGateways", new object[] { addresses, metrics }, "设置默认网关");
    }

    private void ApplyDns(DnsMode mode, IReadOnlyList<string> servers)
    {
        if (mode == DnsMode.Static && servers.Count > 0)
        {
            Invoke(
                "SetDNSServerSearchOrder",
                new object[] { servers.ToArray() },
                "设置 DNS 服务器");
        }
        else
        {
            Invoke(
                "SetDNSServerSearchOrder",
                new object[] { null! },
                "恢复自动 DNS");
        }
    }

    private void Invoke(string method, object[]? args, string description)
    {
        object? result;
        try
        {
            result = _config.InvokeMethod(method, args);
        }
        catch (ManagementException ex)
        {
            throw new WmiOperationException($"{description}失败：{ex.Message}", inner: ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new WmiOperationException($"{description}失败：需要管理员权限。", inner: ex);
        }

        if (result is null)
        {
            return;
        }

        var code = Convert.ToUInt32(result, CultureInfo.InvariantCulture);
        if (code is 0 or 1)
        {
            // 1 means "success, reboot required"; the read-back check decides whether the
            // setting is live now, so both codes count as a successful call.
            return;
        }

        throw new WmiOperationException($"{description}失败（WMI 返回码 {code}）：{Explain(code)}", code);
    }

    private static string Explain(uint code) => code switch
    {
        64 => "该方法在此适配器上不受支持。",
        65 => "未知参数。",
        66 => "参数无效。",
        67 => "网络适配器不可用。",
        68 => "未知故障。",
        69 => "IP 地址无效。",
        70 => "子网掩码无效。",
        71 => "默认网关无效。",
        72 => "DNS 服务器无效。",
        78 => "DNS 服务器地址无效。",
        80 => "无法启用 DHCP。",
        81 => "该适配器当前未启用 DHCP。",
        82 => "网关重复。",
        83 => "DNS 服务器重复。",
        84 => "WINS 服务器重复。",
        85 => "IP 地址重复。",
        86 => "子网掩码重复。",
        91 => "访问被拒绝：需要以管理员身份运行。",
        94 => "无法设置该适配器的网络参数。",
        100 => "参数无效或不受支持。",
        2147786788 => "访问被拒绝：需要以管理员身份运行。",
        2147786789 => "该网络适配器当前不可配置。",
        _ => "Windows 未提供该返回码的说明。"
    };

    public void Dispose()
    {
        _config.Dispose();
        _collection.Dispose();
        _searcher.Dispose();
    }
}

internal static class Ipv4Mask
{
    public static string FromPrefixLength(int prefixLength)
    {
        var prefix = Math.Clamp(prefixLength, 0, 32);
        var bits = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(bits >> 24) & 0xFF}.{(bits >> 16) & 0xFF}.{(bits >> 8) & 0xFF}.{bits & 0xFF}");
    }
}

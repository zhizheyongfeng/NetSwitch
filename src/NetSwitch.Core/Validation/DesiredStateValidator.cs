using System.Net;
using System.Net.Sockets;
using NetSwitch.Core.Models;

namespace NetSwitch.Core.Validation;

/// <summary>
/// Pure, side-effect-free validation. The result is suitable for the first stage of
/// the test flow and for save-time validation.
/// </summary>
public static class DesiredStateValidator
{
    public static IReadOnlyList<TestCheck> Validate(DesiredState? desired)
    {
        var checks = new List<TestCheck>();
        if (desired is null)
        {
            checks.Add(TestCheck.Fail("预设内容", "预设没有目标状态。"));
            return checks;
        }

        ValidateIpv4(desired.Ipv4, checks);
        ValidateIpv6(desired.Ipv6, checks);
        ValidateWireless(desired.Wireless, checks);

        return checks;
    }

    public static bool CanApply(IReadOnlyList<TestCheck> checks) =>
        checks.All(c => c.Status != TestStatus.Fail);

    private static void ValidateIpv4(Ipv4Desired? ipv4, List<TestCheck> checks)
    {
        if (ipv4 is null)
        {
            checks.Add(TestCheck.Fail("IPv4", "缺少 IPv4 目标状态。"));
            return;
        }

        if (ipv4.Mode == Ipv4Mode.Static)
        {
            if (ipv4.Addresses.Count == 0)
            {
                checks.Add(TestCheck.Fail("IPv4 地址", "静态模式至少需要一个 IPv4 地址。"));
            }
            else
            {
                var errors = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var address in ipv4.Addresses)
                {
                    if (!TryParseFamily(address.Address, AddressFamily.InterNetwork, out _))
                    {
                        errors.Add($"{address.Address} 不是有效的 IPv4 地址");
                    }
                    else if (address.PrefixLength is < 0 or > 32)
                    {
                        errors.Add($"{address.Address}/{address.PrefixLength} 前缀长度无效");
                    }
                    else if (!seen.Add(address.Address.Trim()))
                    {
                        errors.Add($"{address.Address} 重复");
                    }
                }

                checks.Add(errors.Count == 0
                    ? TestCheck.Pass("IPv4 地址", FormatAddresses(ipv4.Addresses))
                    : TestCheck.Fail("IPv4 地址", string.Join("；", errors)));
            }

            var gatewayErrors = ValidateGateways(ipv4.Gateways, AddressFamily.InterNetwork);
            checks.Add(gatewayErrors.Count == 0
                ? TestCheck.Pass("默认网关", ipv4.Gateways.Count == 0
                    ? "未设置默认网关"
                    : string.Join(", ", ipv4.Gateways.Select(g => g.Metric is null ? g.Address : $"{g.Address} (metric {g.Metric})")))
                : TestCheck.Fail("默认网关", string.Join("；", gatewayErrors)));
        }
        else
        {
            if (ipv4.Addresses.Count > 0 || ipv4.Gateways.Count > 0)
            {
                checks.Add(TestCheck.Warn(
                    "IPv4 地址",
                    "DHCP 模式会忽略静态地址和网关；请确认这是预期行为。"));
            }
            else
            {
                checks.Add(TestCheck.Pass("IPv4 模式", "DHCP 自动获取地址与网关"));
            }
        }

        if (ipv4.DnsMode == DnsMode.Static)
        {
            var errors = ValidateDnsServers(ipv4.DnsServers);
            checks.Add(errors.Count == 0
                ? TestCheck.Pass("IPv4 DNS", string.Join(", ", ipv4.DnsServers))
                : TestCheck.Fail("IPv4 DNS", string.Join("；", errors)));
        }
        else
        {
            checks.Add(TestCheck.Pass("IPv4 DNS", "自动跟随 DHCP"));
        }

        ValidateMetric(ipv4.MetricMode, ipv4.InterfaceMetric, "IPv4 接口跃点", checks);
    }

    private static void ValidateIpv6(Ipv6Desired? ipv6, List<TestCheck> checks)
    {
        if (ipv6 is null || ipv6.Mode == Ipv6Mode.Keep)
        {
            checks.Add(TestCheck.Skip("IPv6", "预设不管理 IPv6"));
            return;
        }

        if (ipv6.Mode == Ipv6Mode.Static)
        {
            if (ipv6.Addresses.Count == 0)
            {
                checks.Add(TestCheck.Fail("IPv6 地址", "静态模式至少需要一个 IPv6 地址。"));
            }
            else
            {
                var errors = new List<string>();
                foreach (var address in ipv6.Addresses)
                {
                    if (!TryParseFamily(address.Address, AddressFamily.InterNetworkV6, out _))
                    {
                        errors.Add($"{address.Address} 不是有效的 IPv6 地址");
                    }
                    else if (address.PrefixLength is < 0 or > 128)
                    {
                        errors.Add($"{address.Address}/{address.PrefixLength} 前缀长度无效");
                    }
                }

                checks.Add(errors.Count == 0
                    ? TestCheck.Pass("IPv6 地址", FormatAddresses(ipv6.Addresses))
                    : TestCheck.Fail("IPv6 地址", string.Join("；", errors)));
            }

            var gatewayErrors = ValidateGateways(ipv6.Gateways, AddressFamily.InterNetworkV6);
            checks.Add(gatewayErrors.Count == 0
                ? TestCheck.Pass("IPv6 网关", ipv6.Gateways.Count == 0 ? "未设置" : string.Join(", ", ipv6.Gateways.Select(g => g.Address)))
                : TestCheck.Fail("IPv6 网关", string.Join("；", gatewayErrors)));
        }
        else if (ipv6.Addresses.Count > 0 || ipv6.Gateways.Count > 0)
        {
            checks.Add(TestCheck.Warn(
                "IPv6 地址",
                $"{ipv6.Mode} 模式会忽略静态地址和网关。"));
        }
        else
        {
            checks.Add(TestCheck.Pass("IPv6 模式", ipv6.Mode == Ipv6Mode.Auto ? "自动获取" : "禁用绑定"));
        }

        if (ipv6.DnsMode == DnsMode.Static)
        {
            var errors = ValidateDnsServers(ipv6.DnsServers, allowIpv4: false, allowIpv6: true);
            checks.Add(errors.Count == 0
                ? TestCheck.Pass("IPv6 DNS", string.Join(", ", ipv6.DnsServers))
                : TestCheck.Fail("IPv6 DNS", string.Join("；", errors)));
        }
        else
        {
            checks.Add(TestCheck.Pass("IPv6 DNS", "自动"));
        }

        ValidateMetric(ipv6.MetricMode, ipv6.InterfaceMetric, "IPv6 接口跃点", checks);
    }

    private static void ValidateWireless(WirelessDesired? wireless, List<TestCheck> checks)
    {
        if (wireless is null || wireless.Action == WirelessAction.Keep)
        {
            checks.Add(TestCheck.Skip("Wi-Fi", "预设不管理 Wi-Fi"));
            return;
        }

        if (wireless.Action == WirelessAction.Connect &&
            string.IsNullOrWhiteSpace(wireless.SavedProfileName))
        {
            checks.Add(TestCheck.Fail("Wi-Fi Profile", "连接动作必须指定已保存的 Profile。"));
            return;
        }

        checks.Add(TestCheck.Pass(
            "Wi-Fi",
            wireless.Action == WirelessAction.Connect
                ? $"连接已保存的 Profile：{wireless.SavedProfileName}"
                : "断开当前 Wi-Fi"));
    }

    private static List<string> ValidateGateways(IReadOnlyList<Gateway> gateways, AddressFamily family)
    {
        var errors = new List<string>();
        foreach (var gateway in gateways)
        {
            if (!TryParseFamily(gateway.Address, family, out _))
            {
                errors.Add($"{gateway.Address} 不是有效地址");
            }

            if (gateway.Metric is < 1 or > 9999)
            {
                errors.Add($"{gateway.Address} 的 metric 必须在 1–9999 之间");
            }
        }

        return errors;
    }

    private static List<string> ValidateDnsServers(
        IReadOnlyList<string> servers,
        bool allowIpv4 = true,
        bool allowIpv6 = true)
    {
        var errors = new List<string>();
        if (servers.Count == 0)
        {
            errors.Add("静态 DNS 至少需要一个服务器");
        }

        foreach (var server in servers)
        {
            if (!IPAddress.TryParse(server, out var parsed))
            {
                errors.Add($"{server} 不是有效 IP 地址");
                continue;
            }

            if ((parsed.AddressFamily == AddressFamily.InterNetwork && !allowIpv4) ||
                (parsed.AddressFamily == AddressFamily.InterNetworkV6 && !allowIpv6))
            {
                errors.Add($"{server} 的地址族不适用于此字段");
            }
        }

        return errors;
    }

    private static void ValidateMetric(
        MetricMode mode,
        int? interfaceMetric,
        string name,
        List<TestCheck> checks)
    {
        switch (mode)
        {
            case MetricMode.Manual when interfaceMetric is < 1 or > 9999:
                checks.Add(TestCheck.Fail(name, "固定跃点必须在 1–9999 之间。"));
                break;
            case MetricMode.Manual:
                checks.Add(TestCheck.Pass(name, $"固定为 {interfaceMetric}"));
                break;
            case MetricMode.Automatic:
                checks.Add(TestCheck.Pass(name, "自动"));
                break;
            default:
                checks.Add(TestCheck.Skip(name, "保持当前值"));
                break;
        }
    }

    private static bool TryParseFamily(string text, AddressFamily family, out IPAddress? address) =>
        IPAddress.TryParse(text, out address) && address.AddressFamily == family;

    private static string FormatAddresses(IReadOnlyList<Ipv4Address> addresses) =>
        string.Join(", ", addresses.Select(a => $"{a.Address}/{a.PrefixLength}"));
}

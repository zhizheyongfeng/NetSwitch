using System.Globalization;
using NetSwitch.Core.Models;

namespace NetSwitch.Core.Validation;

/// <summary>
/// Read-back verification with human-readable differences. Unlike
/// <see cref="PresetFingerprint"/> it explains what did not match, which makes a failed
/// apply actionable and gives rollback diagnostics.
/// </summary>
public static class DesiredStateVerifier
{
    public static VerificationResult Verify(DesiredState desired, AdapterState actual)
    {
        var issues = new List<VerificationIssue>();

        VerifyIpv4(desired.Ipv4, actual.Ipv4, issues);

        if (desired.Ipv6 is not null && desired.Ipv6.Mode != Ipv6Mode.Keep)
        {
            VerifyIpv6(desired.Ipv6, actual.Ipv6, issues);
        }

        if (desired.Wireless is not null && desired.Wireless.Action != WirelessAction.Keep)
        {
            VerifyWireless(desired.Wireless, actual.Wireless, issues);
        }

        return new VerificationResult(issues);
    }

    private static void VerifyIpv4(Ipv4Desired desired, Ipv4State actual, List<VerificationIssue> issues)
    {
        if (desired.Mode == Ipv4Mode.Dhcp)
        {
            if (!actual.DhcpEnabled)
            {
                issues.Add(new VerificationIssue("IPv4 模式", "DHCP", "静态"));
            }

            var usable = actual.Addresses
                .Where(a => !a.Address.StartsWith("169.254.", StringComparison.Ordinal))
                .ToArray();
            if (usable.Length == 0)
            {
                issues.Add(new VerificationIssue("IPv4 地址", "至少一个 DHCP 地址", "没有可用地址"));
            }
        }
        else
        {
            if (actual.DhcpEnabled)
            {
                issues.Add(new VerificationIssue("IPv4 模式", "静态", "DHCP"));
            }

            CompareAddresses(desired.Addresses, actual.Addresses, "IPv4 地址", issues);
            CompareGateways(desired.Gateways, actual.Gateways, "默认网关", issues);
        }

        VerifyDns(desired.DnsMode, desired.DnsServers, actual.DnsMode, actual.DnsServers, "DNS", issues);
        VerifyMetric(
            desired.MetricMode,
            desired.InterfaceMetric,
            actual.AutomaticMetric,
            actual.InterfaceMetric,
            "IPv4 接口跃点",
            issues);
    }

    private static void VerifyIpv6(Ipv6Desired desired, Ipv6State actual, List<VerificationIssue> issues)
    {
        switch (desired.Mode)
        {
            case Ipv6Mode.Disabled:
                if (actual.BindingEnabled)
                {
                    issues.Add(new VerificationIssue("IPv6 绑定", "已禁用", "仍启用"));
                }

                return;

            case Ipv6Mode.Auto:
                if (!actual.BindingEnabled)
                {
                    issues.Add(new VerificationIssue("IPv6 绑定", "已启用", "已禁用"));
                }

                if (actual.Mode == Ipv6Mode.Static)
                {
                    issues.Add(new VerificationIssue("IPv6 模式", "自动获取", "仍有静态地址"));
                }

                if (desired.DnsMode == DnsMode.Static)
                {
                    VerifyDns(
                        desired.DnsMode,
                        desired.DnsServers,
                        actual.DnsMode,
                        actual.DnsServers,
                        "IPv6 DNS",
                        issues);
                }

                VerifyMetric(
                    desired.MetricMode,
                    desired.InterfaceMetric,
                    actual.AutomaticMetric,
                    actual.InterfaceMetric,
                    "IPv6 接口跃点",
                    issues);
                return;

            case Ipv6Mode.Static:
                if (!actual.BindingEnabled)
                {
                    issues.Add(new VerificationIssue("IPv6 绑定", "已启用", "已禁用"));
                }

                CompareAddresses(
                    desired.Addresses,
                    Ipv6AddressUtil.ComparableAddresses(desired.Addresses, actual.Addresses),
                    "IPv6 地址",
                    issues);
                CompareGateways(desired.Gateways, actual.Gateways, "IPv6 网关", issues);
                VerifyDns(
                    desired.DnsMode,
                    desired.DnsServers,
                    actual.DnsMode,
                    actual.DnsServers,
                    "IPv6 DNS",
                    issues);
                VerifyMetric(
                    desired.MetricMode,
                    desired.InterfaceMetric,
                    actual.AutomaticMetric,
                    actual.InterfaceMetric,
                    "IPv6 接口跃点",
                    issues);
                return;
        }
    }

    private static void VerifyWireless(
        WirelessDesired desired,
        WirelessState? actual,
        List<VerificationIssue> issues)
    {
        if (desired.Action == WirelessAction.Disconnect)
        {
            if (actual is { IsConnected: true })
            {
                issues.Add(new VerificationIssue("Wi-Fi", "未连接", actual.ActiveProfileName ?? "已连接"));
            }

            return;
        }

        if (actual is not { IsConnected: true })
        {
            issues.Add(new VerificationIssue("Wi-Fi", $"连接 {desired.SavedProfileName}", "未连接"));
            return;
        }

        if (!string.Equals(actual.ActiveProfileName, desired.SavedProfileName, StringComparison.Ordinal))
        {
            issues.Add(new VerificationIssue(
                "Wi-Fi",
                desired.SavedProfileName ?? "—",
                actual.ActiveProfileName ?? "—"));
        }
    }

    private static void CompareAddresses(
        IReadOnlyList<Ipv4Address> expected,
        IReadOnlyList<Ipv4Address> actual,
        string field,
        List<VerificationIssue> issues)
    {
        var left = expected
            .Select(a => $"{a.Address}/{a.PrefixLength}")
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var right = actual
            .Select(a => $"{a.Address}/{a.PrefixLength}")
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new VerificationIssue(field, JoinOrDash(left), JoinOrDash(right)));
        }
    }

    private static void CompareGateways(
        IReadOnlyList<Gateway> expected,
        IReadOnlyList<Gateway> actual,
        string field,
        List<VerificationIssue> issues)
    {
        var left = expected.OrderBy(g => g.Address, StringComparer.OrdinalIgnoreCase).ToArray();
        var right = actual.OrderBy(g => g.Address, StringComparer.OrdinalIgnoreCase).ToArray();

        if (left.Length != right.Length)
        {
            issues.Add(new VerificationIssue(field, FormatGateways(left), FormatGateways(right)));
            return;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i].Address, right[i].Address, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new VerificationIssue(field, FormatGateways(left), FormatGateways(right)));
                return;
            }

            // Gateway metric is only meaningful when both sides report one.
            if (left[i].Metric is { } expectedMetric &&
                right[i].Metric is { } actualMetric &&
                expectedMetric != actualMetric)
            {
                issues.Add(new VerificationIssue(
                    "网关跃点",
                    $"{left[i].Address} · {expectedMetric}",
                    $"{right[i].Address} · {actualMetric}"));
                return;
            }
        }
    }

    private static void VerifyDns(
        DnsMode expectedMode,
        IReadOnlyList<string> expectedServers,
        DnsMode actualMode,
        IReadOnlyList<string> actualServers,
        string field,
        List<VerificationIssue> issues)
    {
        if (expectedMode == DnsMode.Auto)
        {
            if (actualMode != DnsMode.Auto)
            {
                issues.Add(new VerificationIssue(field, "自动", JoinOrDash(actualServers)));
            }

            return;
        }

        if (actualMode != DnsMode.Static ||
            !expectedServers.SequenceEqual(actualServers, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new VerificationIssue(
                field,
                JoinOrDash(expectedServers),
                actualMode == DnsMode.Auto ? "自动" : JoinOrDash(actualServers)));
        }
    }

    private static void VerifyMetric(
        MetricMode expectedMode,
        int? expectedMetric,
        bool actualAutomatic,
        int actualMetric,
        string field,
        List<VerificationIssue> issues)
    {
        switch (expectedMode)
        {
            case MetricMode.Automatic when !actualAutomatic:
                issues.Add(new VerificationIssue(field, "自动", actualMetric.ToString(CultureInfo.InvariantCulture)));
                break;
            case MetricMode.Manual when actualAutomatic || expectedMetric != actualMetric:
                issues.Add(new VerificationIssue(
                    field,
                    expectedMetric?.ToString(CultureInfo.InvariantCulture) ?? "固定",
                    actualAutomatic ? "自动" : actualMetric.ToString(CultureInfo.InvariantCulture)));
                break;
        }
    }

    private static string FormatGateways(IReadOnlyList<Gateway> gateways) =>
        gateways.Count == 0
            ? "未设置"
            : JoinOrDash(gateways
                .Select(g => g.Metric is null ? g.Address : $"{g.Address} ({g.Metric})")
                .ToArray());

    private static string JoinOrDash(IReadOnlyList<string> values) =>
        values.Count == 0 ? "未设置" : string.Join(", ", values);
}

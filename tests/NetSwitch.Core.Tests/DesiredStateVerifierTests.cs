using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;
using Xunit;

namespace NetSwitch.Core.Tests;

public sealed class DesiredStateVerifierTests
{
    private static readonly AdapterKey Key = new("windows", "{ABC}", "AA-BB", "以太网");

    [Fact]
    public void MatchingStaticStateHasNoIssues()
    {
        var desired = StaticDesired();
        var actual = State(
            dhcp: false,
            addresses: new[] { new Ipv4Address("192.168.2.100", 24) },
            gateways: new[] { new Gateway("192.168.2.1") },
            dnsMode: DnsMode.Static,
            dns: new[] { "192.168.2.1", "223.5.5.5" },
            automaticMetric: true,
            metric: 25);

        var result = DesiredStateVerifier.Verify(desired, actual);

        Assert.True(result.IsMatch);
    }

    [Fact]
    public void AddressAndDnsMismatchesAreReported()
    {
        var desired = StaticDesired();
        var actual = State(
            dhcp: false,
            addresses: new[] { new Ipv4Address("192.168.2.101", 24) },
            gateways: new[] { new Gateway("192.168.2.1") },
            dnsMode: DnsMode.Auto,
            dns: new[] { "10.0.0.1" },
            automaticMetric: true,
            metric: 25);

        var result = DesiredStateVerifier.Verify(desired, actual);

        Assert.False(result.IsMatch);
        Assert.Contains(result.Issues, i => i.Field == "IPv4 地址");
        Assert.Contains(result.Issues, i => i.Field == "DNS");
    }

    [Fact]
    public void DhcpWithoutUsableAddressFails()
    {
        var desired = DesiredState.Dhcp();
        var actual = State(
            dhcp: true,
            addresses: new[] { new Ipv4Address("169.254.10.20", 16) },
            gateways: Array.Empty<Gateway>(),
            dnsMode: DnsMode.Auto,
            dns: Array.Empty<string>(),
            automaticMetric: true,
            metric: 25);

        var result = DesiredStateVerifier.Verify(desired, actual);

        Assert.False(result.IsMatch);
        Assert.Contains(result.Issues, i => i.Field == "IPv4 地址");
    }

    [Fact]
    public void ManualMetricMismatchIsReported()
    {
        var desired = StaticDesired() with
        {
            Ipv4 = StaticDesired().Ipv4 with
            {
                MetricMode = MetricMode.Manual,
                InterfaceMetric = 7
            }
        };

        var actual = State(
            dhcp: false,
            addresses: new[] { new Ipv4Address("192.168.2.100", 24) },
            gateways: new[] { new Gateway("192.168.2.1") },
            dnsMode: DnsMode.Static,
            dns: new[] { "192.168.2.1", "223.5.5.5" },
            automaticMetric: true,
            metric: 25);

        var result = DesiredStateVerifier.Verify(desired, actual);

        Assert.Contains(result.Issues, i => i.Field == "IPv4 接口跃点");
    }

    [Fact]
    public void GatewayMetricIsComparedOnlyWhenBothSidesHaveOne()
    {
        var desired = StaticDesired();
        var withoutMetric = State(
            dhcp: false,
            addresses: new[] { new Ipv4Address("192.168.2.100", 24) },
            gateways: new[] { new Gateway("192.168.2.1") },
            dnsMode: DnsMode.Static,
            dns: new[] { "192.168.2.1", "223.5.5.5" },
            automaticMetric: true,
            metric: 25);

        Assert.True(DesiredStateVerifier.Verify(desired, withoutMetric).IsMatch);

        var withOtherMetric = withoutMetric with
        {
            Ipv4 = withoutMetric.Ipv4 with { Gateways = new[] { new Gateway("192.168.2.1", 20) } }
        };

        Assert.Contains(
            DesiredStateVerifier.Verify(desired, withOtherMetric).Issues,
            i => i.Field == "网关跃点");
    }

    [Fact]
    public void Ipv6KeepIsIgnoredButDisabledIsChecked()
    {
        var desired = StaticDesired();
        var actual = State(
            dhcp: false,
            addresses: new[] { new Ipv4Address("192.168.2.100", 24) },
            gateways: new[] { new Gateway("192.168.2.1") },
            dnsMode: DnsMode.Static,
            dns: new[] { "192.168.2.1", "223.5.5.5" },
            automaticMetric: true,
            metric: 25);

        Assert.True(DesiredStateVerifier.Verify(desired, actual).IsMatch);

        var disabled = desired with { Ipv6 = Ipv6Desired.Keep() with { Mode = Ipv6Mode.Disabled } };
        Assert.Contains(
            DesiredStateVerifier.Verify(disabled, actual).Issues,
            i => i.Field == "IPv6 绑定");
    }

    [Fact]
    public void WirelessConnectChecksActiveProfile()
    {
        var desired = DesiredState.Dhcp() with
        {
            Wireless = new WirelessDesired(WirelessAction.Connect, "公司-WiFi")
        };

        var disconnected = State(
            dhcp: true,
            addresses: new[] { new Ipv4Address("192.168.1.20", 24) },
            gateways: new[] { new Gateway("192.168.1.1") },
            dnsMode: DnsMode.Auto,
            dns: new[] { "192.168.1.1" },
            automaticMetric: true,
            metric: 25);

        Assert.Contains(
            DesiredStateVerifier.Verify(desired, disconnected).Issues,
            i => i.Field == "Wi-Fi");

        var connected = disconnected with
        {
            Wireless = new WirelessState(true, "公司-WiFi", "公司-WiFi")
        };

        Assert.True(DesiredStateVerifier.Verify(desired, connected).IsMatch);
    }

    private static DesiredState StaticDesired() => new(
        new Ipv4Desired(
            Ipv4Mode.Static,
            new[] { new Ipv4Address("192.168.2.100", 24) },
            new[] { new Gateway("192.168.2.1", 10) },
            DnsMode.Static,
            new[] { "192.168.2.1", "223.5.5.5" },
            MetricMode.Automatic,
            null),
        Ipv6Desired.Keep(),
        WirelessDesired.Keep());

    private static AdapterState State(
        bool dhcp,
        IReadOnlyList<Ipv4Address> addresses,
        IReadOnlyList<Gateway> gateways,
        DnsMode dnsMode,
        IReadOnlyList<string> dns,
        bool automaticMetric,
        int metric) =>
        new(
            Key,
            DateTimeOffset.Now,
            new Ipv4State(dhcp, addresses, gateways, dnsMode, dns, automaticMetric, metric),
            new Ipv6State(true, Array.Empty<Ipv4Address>(), Array.Empty<Gateway>(), Array.Empty<string>(), true, 25),
            null);
}

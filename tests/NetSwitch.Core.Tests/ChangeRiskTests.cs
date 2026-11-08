using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;
using Xunit;

namespace NetSwitch.Core.Tests;

public sealed class ChangeRiskTests
{
    private static AdapterState State(
        Ipv4State? ipv4 = null,
        Ipv6State? ipv6 = null,
        WirelessState? wireless = null) =>
        new(
            new AdapterKey("windows", Guid.NewGuid().ToString(), "00:11:22:33:44:55", "测试网卡"),
            DateTimeOffset.Now,
            ipv4 ?? DhcpIpv4(),
            ipv6 ?? AutoIpv6(),
            wireless);

    private static Ipv4State DhcpIpv4() => new(
        true,
        new[] { new Ipv4Address("192.168.1.10", 24) },
        new[] { new Gateway("192.168.1.1") },
        DnsMode.Auto,
        Array.Empty<string>(),
        true,
        25);

    private static Ipv4State StaticIpv4(
        string address = "192.168.1.10",
        string gateway = "192.168.1.1",
        DnsMode dnsMode = DnsMode.Auto,
        params string[] dns) => new(
        false,
        new[] { new Ipv4Address(address, 24) },
        string.IsNullOrWhiteSpace(gateway)
            ? Array.Empty<Gateway>()
            : new[] { new Gateway(gateway) },
        dnsMode,
        dns,
        true,
        25);

    private static Ipv6State AutoIpv6() => new(
        true,
        Array.Empty<Ipv4Address>(),
        Array.Empty<Gateway>(),
        Array.Empty<string>(),
        true,
        25);

    private static Ipv6State DisabledIpv6() => new(
        false,
        Array.Empty<Ipv4Address>(),
        Array.Empty<Gateway>(),
        Array.Empty<string>(),
        true,
        25,
        DnsMode.Auto,
        Ipv6Mode.Disabled);

    private static DesiredState DhcpDesired(DnsMode dns = DnsMode.Auto, params string[] servers) =>
        new(new Ipv4Desired(
            Ipv4Mode.Dhcp,
            Array.Empty<Ipv4Address>(),
            Array.Empty<Gateway>(),
            dns,
            servers,
            MetricMode.Keep,
            null),
            Ipv6Desired.Keep());

    private static DesiredState StaticDesired(
        IReadOnlyList<Ipv4Address>? addresses = null,
        string? gateway = "192.168.1.1",
        DnsMode dnsMode = DnsMode.Auto,
        params string[] dns)
    {
        var list = addresses ?? new[] { new Ipv4Address("192.168.1.10", 24) };
        return new DesiredState(
            new Ipv4Desired(
                Ipv4Mode.Static,
                list,
                string.IsNullOrWhiteSpace(gateway)
                    ? Array.Empty<Gateway>()
                    : new[] { new Gateway(gateway) },
                dnsMode,
                dns,
                MetricMode.Keep,
                null),
            Ipv6Desired.Keep());
    }

    [Fact]
    public void DnsOnlyChangeIsLowRisk()
    {
        var desired = DhcpDesired(DnsMode.Static, "1.1.1.1");
        var current = State(DhcpIpv4());

        var risk = ChangeRisk.Evaluate(desired, current, isOnlyConnectedAdapter: true);

        Assert.False(risk.IsDangerous);
        Assert.Empty(risk.Reasons);
    }

    [Fact]
    public void AddingSecondaryAddressIsLowRisk()
    {
        var current = State(StaticIpv4());
        var desired = StaticDesired(new[]
        {
            new Ipv4Address("192.168.1.10", 24),
            new Ipv4Address("192.168.1.11", 24)
        });

        var risk = ChangeRisk.Evaluate(desired, current, isOnlyConnectedAdapter: true);

        Assert.False(risk.IsDangerous);
    }

    [Fact]
    public void ChangingPrimaryAddressIsDangerous()
    {
        var current = State(StaticIpv4("192.168.1.10"));
        var desired = StaticDesired(new[] { new Ipv4Address("192.168.1.20", 24) });

        var risk = ChangeRisk.Evaluate(desired, current, isOnlyConnectedAdapter: false);

        Assert.True(risk.IsDangerous);
        Assert.Contains("主 IPv4", risk.ReasonText);
    }

    [Fact]
    public void ChangingGatewayIsDangerous()
    {
        var current = State(StaticIpv4(gateway: "192.168.1.1"));
        var desired = StaticDesired(gateway: "192.168.1.254");

        var risk = ChangeRisk.Evaluate(desired, current, isOnlyConnectedAdapter: false);

        Assert.True(risk.IsDangerous);
        Assert.Contains("网关", risk.ReasonText);
    }

    [Fact]
    public void SettingGatewayWhereNoneExistedIsDangerous()
    {
        var current = State(StaticIpv4(gateway: ""));
        var desired = StaticDesired(gateway: "192.168.1.1");

        var risk = ChangeRisk.Evaluate(desired, current, isOnlyConnectedAdapter: false);

        Assert.True(risk.IsDangerous);
    }

    [Fact]
    public void SwitchingStaticToDhcpIsDangerous()
    {
        var current = State(StaticIpv4());
        var desired = DhcpDesired();

        var risk = ChangeRisk.Evaluate(desired, current, isOnlyConnectedAdapter: false);

        Assert.True(risk.IsDangerous);
    }

    [Fact]
    public void SwitchingDhcpToStaticWithSameAddressIsLowRisk()
    {
        var current = State(DhcpIpv4());
        var desired = StaticDesired(new[] { new Ipv4Address("192.168.1.10", 24) });

        var risk = ChangeRisk.Evaluate(desired, current, isOnlyConnectedAdapter: false);

        Assert.False(risk.IsDangerous);
    }

    [Fact]
    public void DisablingIpv6IsDangerous()
    {
        var current = State(ipv6: AutoIpv6());
        var desired = DhcpDesired() with { Ipv6 = Ipv6Desired.Disabled() };

        var risk = ChangeRisk.Evaluate(desired, current, isOnlyConnectedAdapter: false);

        Assert.True(risk.IsDangerous);
        Assert.Contains("IPv6", risk.ReasonText);
    }

    [Fact]
    public void ReapplyingDisabledIpv6IsLowRisk()
    {
        var current = State(ipv6: DisabledIpv6());
        var desired = DhcpDesired() with { Ipv6 = Ipv6Desired.Disabled() };

        var risk = ChangeRisk.Evaluate(desired, current, isOnlyConnectedAdapter: false);

        Assert.False(risk.IsDangerous);
    }

    [Fact]
    public void WifiDisconnectOnOnlyConnectedAdapterIsDangerous()
    {
        var current = State(wireless: new WirelessState(true, "Office", "Office"));
        var desired = DhcpDesired() with
        {
            Wireless = new WirelessDesired(WirelessAction.Disconnect)
        };

        var risk = ChangeRisk.Evaluate(desired, current, isOnlyConnectedAdapter: true);

        Assert.True(risk.IsDangerous);
        Assert.Contains("Wi-Fi", risk.ReasonText);
    }

    [Fact]
    public void WifiDisconnectWithAnotherConnectedAdapterIsLowRisk()
    {
        var current = State(wireless: new WirelessState(true, "Office", "Office"));
        var desired = DhcpDesired() with
        {
            Wireless = new WirelessDesired(WirelessAction.Disconnect)
        };

        var risk = ChangeRisk.Evaluate(desired, current, isOnlyConnectedAdapter: false);

        Assert.False(risk.IsDangerous);
    }

    [Fact]
    public void WifiConnectIsLowRisk()
    {
        var current = State(wireless: new WirelessState(false, null, null));
        var desired = DhcpDesired() with
        {
            Wireless = new WirelessDesired(WirelessAction.Connect, "Office")
        };

        var risk = ChangeRisk.Evaluate(desired, current, isOnlyConnectedAdapter: true);

        Assert.False(risk.IsDangerous);
    }

    [Fact]
    public void UnknownCurrentStateIsConservativelyDangerous()
    {
        var risk = ChangeRisk.Evaluate(DhcpDesired(), current: null, isOnlyConnectedAdapter: false);

        Assert.True(risk.IsDangerous);
    }
}

using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;
using Xunit;

namespace NetSwitch.Core.Tests;

public sealed class PresetFactoryTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 11, 8, 12, 0, 0, TimeSpan.FromHours(8));

    private static AdapterState State(
        Ipv4State? ipv4 = null,
        Ipv6State? ipv6 = null,
        WirelessState? wireless = null) =>
        new(
            new AdapterKey("windows", Guid.NewGuid().ToString(), "00:11:22:33:44:55", "测试网卡"),
            FixedNow,
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

    private static Ipv4State StaticIpv4(DnsMode dnsMode = DnsMode.Static, params string[] dns) => new(
        false,
        new[] { new Ipv4Address("10.0.0.10", 24), new Ipv4Address("10.0.0.11", 24) },
        new[] { new Gateway("10.0.0.1", 5) },
        dnsMode,
        dns,
        false,
        30);

    private static Ipv6State AutoIpv6(DnsMode dnsMode = DnsMode.Auto, params string[] dns) => new(
        true,
        Array.Empty<Ipv4Address>(),
        Array.Empty<Gateway>(),
        dns,
        true,
        25,
        dnsMode,
        Ipv6Mode.Auto);

    private static Ipv6State StaticIpv6(params Ipv4Address[] addresses) => new(
        true,
        addresses,
        new[] { new Gateway("fd00::1") },
        new[] { "fd00::53" },
        false,
        30,
        DnsMode.Static,
        Ipv6Mode.Static);

    [Fact]
    public void FromState_StaticIpv4_CapturesAddressesGatewaysDnsAndMetric()
    {
        var state = State(
            ipv4: StaticIpv4(DnsMode.Static, "10.0.0.53", "10.0.0.54"));

        var preset = PresetFactory.FromState("当前配置", state, FixedNow);

        Assert.Equal(Ipv4Mode.Static, preset.Desired.Ipv4.Mode);
        Assert.Equal(2, preset.Desired.Ipv4.Addresses.Count);
        Assert.Equal(new Gateway("10.0.0.1", 5), preset.Desired.Ipv4.Gateways[0]);
        Assert.Equal(DnsMode.Static, preset.Desired.Ipv4.DnsMode);
        Assert.Equal(new[] { "10.0.0.53", "10.0.0.54" }, preset.Desired.Ipv4.DnsServers);
        Assert.Equal(MetricMode.Manual, preset.Desired.Ipv4.MetricMode);
        Assert.Equal(30, preset.Desired.Ipv4.InterfaceMetric);
    }

    [Fact]
    public void FromState_DhcpWithAutomaticMetric()
    {
        var preset = PresetFactory.FromState("当前配置", State(), FixedNow);

        Assert.Equal(Ipv4Mode.Dhcp, preset.Desired.Ipv4.Mode);
        Assert.Equal(MetricMode.Automatic, preset.Desired.Ipv4.MetricMode);
        Assert.Null(preset.Desired.Ipv4.InterfaceMetric);
    }

    [Fact]
    public void FromState_Ipv6Disabled_ProducesDisabledDesired()
    {
        var ipv6 = AutoIpv6() with
        {
            BindingEnabled = false,
            Mode = Ipv6Mode.Disabled
        };

        var preset = PresetFactory.FromState("当前配置", State(ipv6: ipv6), FixedNow);

        Assert.Equal(Ipv6Mode.Disabled, preset.Desired.Ipv6!.Mode);
    }

    [Fact]
    public void FromState_Ipv6Static_FiltersLinkLocalAddresses()
    {
        var ipv6 = StaticIpv6(
            new Ipv4Address("fe80::1234", 64),
            new Ipv4Address("fd00::10", 64));

        var preset = PresetFactory.FromState("当前配置", State(ipv6: ipv6), FixedNow);

        var desired = preset.Desired.Ipv6!;
        Assert.Equal(Ipv6Mode.Static, desired.Mode);
        Assert.Single(desired.Addresses);
        Assert.Equal("fd00::10", desired.Addresses[0].Address);
        Assert.Equal(DnsMode.Static, desired.DnsMode);
        Assert.Equal("fd00::53", desired.DnsServers[0]);
        Assert.Equal(MetricMode.Manual, desired.MetricMode);
        Assert.Equal(30, desired.InterfaceMetric);
    }

    [Fact]
    public void FromState_Ipv6Auto_KeepsStaticDns()
    {
        var ipv6 = AutoIpv6(DnsMode.Static, "fd00::53");

        var preset = PresetFactory.FromState("当前配置", State(ipv6: ipv6), FixedNow);

        var desired = preset.Desired.Ipv6!;
        Assert.Equal(Ipv6Mode.Auto, desired.Mode);
        Assert.Equal(DnsMode.Static, desired.DnsMode);
        Assert.Equal("fd00::53", desired.DnsServers[0]);
        Assert.Empty(desired.Addresses);
    }

    [Fact]
    public void FromState_ConnectedWifi_CapturesProfile()
    {
        var state = State(wireless: new WirelessState(true, "Home", "Home"));

        var preset = PresetFactory.FromState("当前配置", state, FixedNow);

        Assert.Equal(WirelessAction.Connect, preset.Desired.Wireless!.Action);
        Assert.Equal("Home", preset.Desired.Wireless.SavedProfileName);
    }

    [Fact]
    public void FromState_DisconnectedWifi_KeepsCurrent()
    {
        var state = State(wireless: new WirelessState(false, null, null));

        var preset = PresetFactory.FromState("当前配置", state, FixedNow);

        Assert.Equal(WirelessAction.Keep, preset.Desired.Wireless!.Action);
    }

    [Fact]
    public void CloneAsNew_KeepsDesiredButChangesIdentity()
    {
        var source = new Preset(
            "old-id",
            "公司网络",
            "备注",
            new DesiredState(Ipv4Desired.Dhcp()),
            FixedNow.AddDays(-2),
            FixedNow.AddDays(-1));

        var clone = PresetFactory.CloneAsNew(source, "公司网络（副本）", FixedNow);

        Assert.NotEqual(source.Id, clone.Id);
        Assert.Equal("公司网络（副本）", clone.Name);
        Assert.Equal(source.Notes, clone.Notes);
        Assert.Equal(source.Desired, clone.Desired);
        Assert.Equal(FixedNow, clone.CreatedAt);
        Assert.Equal(FixedNow, clone.UpdatedAt);
    }
}

using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;
using NetSwitch.Platform.Fake;
using Xunit;

namespace NetSwitch.Core.Tests;

/// <summary>
/// M3 behaviour: binding on/off, static IPv6 writes, ignoring the always-present
/// link-local address, and snapshot metadata that tells a rollback whether the
/// adapter was static or automatic before the transaction.
/// </summary>
public sealed class Ipv6BehaviorTests
{
    private static readonly AdapterKey Key = new("windows", "{ABC}", "AA-BB", "以太网");

    [Fact]
    public void StaticVerificationIgnoresAutomaticLinkLocal()
    {
        var desired = DhcpWithIpv6(StaticIpv6());
        var actual = DhcpState(Ipv6(
            binding: true,
            addresses: new[]
            {
                new Ipv4Address("fe80::a1b2:c3d4:e5f6:0001", 64),
                new Ipv4Address("240e:3a1:2b3c:4d5e::100", 64)
            },
            gateways: new[] { new Gateway("fe80::1") },
            dnsMode: DnsMode.Static,
            dns: new[] { "240e:3a1:2b3c:4d5e::8888" },
            mode: Ipv6Mode.Static));

        Assert.True(DesiredStateVerifier.Verify(desired, actual).IsMatch);
    }

    [Fact]
    public void StaticVerificationReportsMissingGlobalAddress()
    {
        var desired = DhcpWithIpv6(StaticIpv6());
        var actual = DhcpState(Ipv6(
            binding: true,
            addresses: new[] { new Ipv4Address("fe80::a1b2:c3d4:e5f6:0001", 64) },
            mode: Ipv6Mode.Static));

        var result = DesiredStateVerifier.Verify(desired, actual);

        Assert.False(result.IsMatch);
        Assert.Contains(result.Issues, i => i.Field == "IPv6 地址");
    }

    [Fact]
    public void StaticVerificationComparesExplicitlyConfiguredLinkLocal()
    {
        var desired = DhcpWithIpv6(StaticIpv6() with
        {
            Addresses = new[]
            {
                new Ipv4Address("fe80::100", 64),
                new Ipv4Address("240e:3a1:2b3c:4d5e::100", 64)
            }
        });
        var actual = DhcpState(Ipv6(
            binding: true,
            addresses: new[]
            {
                new Ipv4Address("fe80::100", 64),
                new Ipv4Address("240e:3a1:2b3c:4d5e::100", 64)
            },
            gateways: new[] { new Gateway("fe80::1") },
            dnsMode: DnsMode.Static,
            dns: new[] { "240e:3a1:2b3c:4d5e::8888" },
            mode: Ipv6Mode.Static));

        Assert.True(DesiredStateVerifier.Verify(desired, actual).IsMatch);
    }

    [Fact]
    public void DisabledVerificationRequiresBindingOff()
    {
        var desired = DhcpWithIpv6(Ipv6Desired.Disabled());

        var stillBound = DhcpState(Ipv6(binding: true));
        Assert.Contains(
            DesiredStateVerifier.Verify(desired, stillBound).Issues,
            i => i.Field == "IPv6 绑定");

        var unbound = DhcpState(Ipv6(binding: false));
        Assert.True(DesiredStateVerifier.Verify(desired, unbound).IsMatch);
    }

    [Fact]
    public void AutoVerificationRejectsLeftoverStaticAddresses()
    {
        var desired = DhcpWithIpv6(Ipv6Desired.Auto());
        var actual = DhcpState(Ipv6(
            binding: true,
            addresses: new[] { new Ipv4Address("240e:3a1:2b3c:4d5e::100", 64) },
            mode: Ipv6Mode.Static));

        Assert.Contains(
            DesiredStateVerifier.Verify(desired, actual).Issues,
            i => i.Field == "IPv6 模式");
    }

    [Fact]
    public void AutoVerificationChecksStaticDnsOnlyWhenConfigured()
    {
        var auto = Ipv6Desired.Auto();
        var actualAutoDns = DhcpState(Ipv6(binding: true));
        Assert.True(DesiredStateVerifier.Verify(DhcpWithIpv6(auto), actualAutoDns).IsMatch);

        var withDns = auto with
        {
            DnsMode = DnsMode.Static,
            DnsServers = new[] { "240e:3a1:2b3c:4d5e::8888" }
        };

        Assert.Contains(
            DesiredStateVerifier.Verify(DhcpWithIpv6(withDns), actualAutoDns).Issues,
            i => i.Field == "IPv6 DNS");

        var actualStaticDns = DhcpState(Ipv6(
            binding: true,
            dnsMode: DnsMode.Static,
            dns: new[] { "240e:3a1:2b3c:4d5e::8888" }));
        Assert.True(DesiredStateVerifier.Verify(DhcpWithIpv6(withDns), actualStaticDns).IsMatch);
    }

    [Fact]
    public void FingerprintUsesModeAndDnsModeFromTheSnapshot()
    {
        var desired = DhcpWithIpv6(StaticIpv6());
        var matching = DhcpState(Ipv6(
            binding: true,
            addresses: new[]
            {
                new Ipv4Address("fe80::1", 64),
                new Ipv4Address("240e:3a1:2b3c:4d5e::100", 64)
            },
            gateways: new[] { new Gateway("fe80::1") },
            dnsMode: DnsMode.Static,
            dns: new[] { "240e:3a1:2b3c:4d5e::8888" },
            mode: Ipv6Mode.Static));

        Assert.True(PresetFingerprint.Matches(desired, matching));

        var automaticDns = matching with
        {
            Ipv6 = matching.Ipv6 with { DnsMode = DnsMode.Auto, DnsServers = Array.Empty<string>() }
        };
        Assert.False(PresetFingerprint.Matches(desired, automaticDns));

        var autoDesired = DhcpWithIpv6(Ipv6Desired.Auto());
        Assert.False(PresetFingerprint.Matches(autoDesired, matching));
        Assert.True(PresetFingerprint.Matches(
            DhcpWithIpv6(Ipv6Desired.Disabled()),
            DhcpState(Ipv6(binding: false))));
    }

    [Fact]
    public void Ipv6DnsMustUseIpv6Addresses()
    {
        var desired = DhcpWithIpv6(StaticIpv6() with
        {
            DnsServers = new[] { "8.8.8.8" }
        });

        var checks = DesiredStateValidator.Validate(desired);

        Assert.Contains(checks, c => c.Status == TestStatus.Fail && c.Name == "IPv6 DNS");
    }

    [Fact]
    public async Task FakeBackendMovesBetweenStaticAutoAndDisabled()
    {
        var state = FakeNetworkState.CreateDefault();
        var configurator = new FakeNetworkConfigurator(state)
        {
            ApplyDelay = TimeSpan.Zero,
            RestoreDelay = TimeSpan.Zero
        };
        var key = state.GetAdapters().First(a => a.Key.StableId == "fake-ethernet").Key;

        var staticResult = await configurator.ApplyAsync(
            key,
            DhcpWithIpv6(StaticIpv6()),
            ApplyOptions.Default);
        Assert.True(staticResult.Success);

        var applied = state.GetState(key)!;
        Assert.Equal(Ipv6Mode.Static, applied.Ipv6.Mode);
        Assert.Equal(DnsMode.Static, applied.Ipv6.DnsMode);
        Assert.True(applied.Ipv6.BindingEnabled);
        Assert.Equal(
            new[] { "240e:3a1:2b3c:4d5e::100/64" },
            applied.Ipv6.Addresses.Select(a => $"{a.Address}/{a.PrefixLength}"));
        Assert.Equal(
            new[] { "240e:3a1:2b3c:4d5e::8888" },
            applied.Ipv6.DnsServers);

        var autoResult = await configurator.ApplyAsync(
            key,
            DhcpWithIpv6(Ipv6Desired.Auto()),
            ApplyOptions.Default);
        Assert.True(autoResult.Success);

        var automatic = state.GetState(key)!;
        Assert.Equal(Ipv6Mode.Auto, automatic.Ipv6.Mode);
        Assert.Empty(automatic.Ipv6.Addresses);
        Assert.Empty(automatic.Ipv6.Gateways);

        var disabledResult = await configurator.ApplyAsync(
            key,
            DhcpWithIpv6(Ipv6Desired.Disabled()),
            ApplyOptions.Default);
        Assert.True(disabledResult.Success);

        var disabled = state.GetState(key)!;
        Assert.False(disabled.Ipv6.BindingEnabled);
        Assert.Equal(Ipv6Mode.Disabled, disabled.Ipv6.Mode);
        Assert.Empty(disabled.Ipv6.Addresses);
        Assert.Empty(disabled.Ipv6.DnsServers);
    }

    private static DesiredState DhcpWithIpv6(Ipv6Desired ipv6) =>
        new(Ipv4Desired.Dhcp(), ipv6, WirelessDesired.Keep());

    private static Ipv6Desired StaticIpv6() => new(
        Ipv6Mode.Static,
        new[] { new Ipv4Address("240e:3a1:2b3c:4d5e::100", 64) },
        new[] { new Gateway("fe80::1") },
        DnsMode.Static,
        new[] { "240e:3a1:2b3c:4d5e::8888" },
        MetricMode.Keep,
        null);

    private static Ipv6State Ipv6(
        bool binding,
        IReadOnlyList<Ipv4Address>? addresses = null,
        IReadOnlyList<Gateway>? gateways = null,
        DnsMode dnsMode = DnsMode.Auto,
        IReadOnlyList<string>? dns = null,
        Ipv6Mode mode = Ipv6Mode.Auto) =>
        new(
            binding,
            addresses ?? Array.Empty<Ipv4Address>(),
            gateways ?? Array.Empty<Gateway>(),
            dns ?? Array.Empty<string>(),
            AutomaticMetric: true,
            InterfaceMetric: 25,
            DnsMode: dnsMode,
            Mode: mode);

    private static AdapterState DhcpState(Ipv6State ipv6) =>
        new(
            Key,
            DateTimeOffset.Now,
            new Ipv4State(
                true,
                new[] { new Ipv4Address("192.168.1.23", 24) },
                new[] { new Gateway("192.168.1.1") },
                DnsMode.Auto,
                new[] { "192.168.1.1" },
                AutomaticMetric: true,
                InterfaceMetric: 25),
            ipv6,
            null);
}

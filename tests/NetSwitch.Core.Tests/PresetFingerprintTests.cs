using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;
using Xunit;

namespace NetSwitch.Core.Tests;

public sealed class PresetFingerprintTests
{
    private static readonly AdapterKey Key = new("fake", "fake-ethernet", "AA:BB:CC:00:00:01", "以太网");

    [Fact]
    public void DhcpPresetMatchesDhcpState()
    {
        var state = State(
            new Ipv4State(
                true,
                new[] { new Ipv4Address("192.168.1.23", 24) },
                new[] { new Gateway("192.168.1.1"), new Gateway("192.168.1.2", 20) },
                DnsMode.Auto,
                new[] { "192.168.1.1" },
                AutomaticMetric: true,
                InterfaceMetric: 25));

        Assert.True(PresetFingerprint.Matches(DesiredState.Dhcp(), state));
    }

    [Fact]
    public void StaticPresetIgnoresAddressOrderButTracksDns()
    {
        var desired = StaticDesired();
        var state = State(
            new Ipv4State(
                false,
                new[]
                {
                    new Ipv4Address("192.168.11.51", 24),
                    new Ipv4Address("192.168.11.50", 24)
                },
                new[] { new Gateway("192.168.11.1", 10) },
                DnsMode.Static,
                new[] { "223.5.5.5", "119.29.29.29" },
                AutomaticMetric: false,
                InterfaceMetric: 20));

        Assert.True(PresetFingerprint.Matches(desired, state));

        var differentDns = state with
        {
            Ipv4 = state.Ipv4 with { DnsServers = new[] { "8.8.8.8", "1.1.1.1" } }
        };
        Assert.False(PresetFingerprint.Matches(desired, differentDns));
    }

    [Fact]
    public void StaticPresetDoesNotMatchDhcpState()
    {
        var state = State(new Ipv4State(
            true,
            new[] { new Ipv4Address("192.168.1.23", 24) },
            new[] { new Gateway("192.168.1.1") },
            DnsMode.Auto,
            Array.Empty<string>(),
            AutomaticMetric: true,
            InterfaceMetric: 25));

        Assert.False(PresetFingerprint.Matches(StaticDesired(), state));
    }

    [Fact]
    public void KeepIpv6IsIgnored()
    {
        var state = State(
            new Ipv4State(
                true,
                new[] { new Ipv4Address("192.168.1.23", 24) },
                new[] { new Gateway("192.168.1.1") },
                DnsMode.Auto,
                Array.Empty<string>(),
                AutomaticMetric: true,
                InterfaceMetric: 25),
            new Ipv6State(
                false,
                Array.Empty<Ipv4Address>(),
                Array.Empty<Gateway>(),
                Array.Empty<string>(),
                AutomaticMetric: true,
                InterfaceMetric: 25));

        Assert.True(PresetFingerprint.Matches(DesiredState.Dhcp(), state));
    }

    [Fact]
    public void Ipv6AutoIgnoresStaticDnsMismatch()
    {
        var desired = DesiredState.Dhcp() with
        {
            Ipv6 = new Ipv6Desired(
                Ipv6Mode.Auto,
                Array.Empty<Ipv4Address>(),
                Array.Empty<Gateway>(),
                DnsMode.Auto,
                Array.Empty<string>(),
                MetricMode.Keep,
                null)
        };

        var state = State(
            Ipv4StateOf(desired),
            new Ipv6State(
                true,
                new[] { new Ipv4Address("fe80::1", 64) },
                Array.Empty<Gateway>(),
                new[] { "240c::6666" },
                AutomaticMetric: true,
                InterfaceMetric: 25));

        Assert.True(PresetFingerprint.Matches(desired, state));
    }

    [Fact]
    public void WirelessConnectRequiresMatchingActiveProfile()
    {
        var desired = DesiredState.Dhcp() with
        {
            Wireless = new WirelessDesired(WirelessAction.Connect, "公司-WiFi")
        };
        var state = State(Ipv4StateOf(desired)) with
        {
            Wireless = new WirelessState(true, "家庭网络-5G", "家庭网络-5G")
        };

        Assert.False(PresetFingerprint.Matches(desired, state));

        var connected = state with
        {
            Wireless = new WirelessState(true, "公司-WiFi", "公司-WiFi")
        };
        Assert.True(PresetFingerprint.Matches(desired, connected));
    }

    private static DesiredState StaticDesired() =>
        new(
            new Ipv4Desired(
                Ipv4Mode.Static,
                new[]
                {
                    new Ipv4Address("192.168.11.50", 24),
                    new Ipv4Address("192.168.11.51", 24)
                },
                new[] { new Gateway("192.168.11.1", 10) },
                DnsMode.Static,
                new[] { "223.5.5.5", "119.29.29.29" },
                MetricMode.Manual,
                20),
            Ipv6Desired.Keep(),
            WirelessDesired.Keep());

    private static AdapterState State(Ipv4State ipv4, Ipv6State? ipv6 = null) =>
        new(
            Key,
            DateTimeOffset.Now,
            ipv4,
            ipv6 ?? new Ipv6State(
                true,
                Array.Empty<Ipv4Address>(),
                Array.Empty<Gateway>(),
                Array.Empty<string>(),
                AutomaticMetric: true,
                InterfaceMetric: 25),
            null);

    private static Ipv4State Ipv4StateOf(DesiredState desired) =>
        new(
            desired.Ipv4.Mode == Ipv4Mode.Dhcp,
            desired.Ipv4.Mode == Ipv4Mode.Dhcp
                ? new[] { new Ipv4Address("192.168.1.23", 24) }
                : desired.Ipv4.Addresses,
            desired.Ipv4.Mode == Ipv4Mode.Dhcp
                ? new[] { new Gateway("192.168.1.1") }
                : desired.Ipv4.Gateways,
            desired.Ipv4.DnsMode,
            desired.Ipv4.DnsServers,
            AutomaticMetric: true,
            InterfaceMetric: 25);
}

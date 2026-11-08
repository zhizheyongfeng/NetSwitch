using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;
using Xunit;

namespace NetSwitch.Core.Tests;

public sealed class DesiredStateValidatorTests
{
    [Fact]
    public void NullDesiredStateFails()
    {
        var checks = DesiredStateValidator.Validate(null);

        Assert.Contains(checks, c => c.Status == TestStatus.Fail);
        Assert.False(DesiredStateValidator.CanApply(checks));
    }

    [Fact]
    public void ValidStaticConfigurationPasses()
    {
        var desired = StaticDesired();

        var checks = DesiredStateValidator.Validate(desired);

        Assert.DoesNotContain(checks, c => c.Status == TestStatus.Fail);
        Assert.True(DesiredStateValidator.CanApply(checks));
    }

    [Fact]
    public void InvalidPrefixLengthFails()
    {
        var desired = StaticDesired() with
        {
            Ipv4 = StaticDesired().Ipv4 with
            {
                Addresses = new[] { new Ipv4Address("192.168.1.10", 33) }
            }
        };

        var checks = DesiredStateValidator.Validate(desired);

        Assert.Contains(checks, c => c.Status == TestStatus.Fail && c.Name == "IPv4 地址");
        Assert.False(DesiredStateValidator.CanApply(checks));
    }

    [Fact]
    public void StaticDnsRequiresAtLeastOneServer()
    {
        var desired = StaticDesired() with
        {
            Ipv4 = StaticDesired().Ipv4 with
            {
                DnsMode = DnsMode.Static,
                DnsServers = Array.Empty<string>()
            }
        };

        var checks = DesiredStateValidator.Validate(desired);

        Assert.Contains(checks, c => c.Status == TestStatus.Fail && c.Name == "IPv4 DNS");
    }

    [Fact]
    public void WrongAddressFamilyInIpv6SectionFails()
    {
        var desired = StaticDesired() with
        {
            Ipv6 = new Ipv6Desired(
                Ipv6Mode.Static,
                new[] { new Ipv4Address("192.168.1.10", 64) },
                Array.Empty<Gateway>(),
                DnsMode.Auto,
                Array.Empty<string>(),
                MetricMode.Keep,
                null)
        };

        var checks = DesiredStateValidator.Validate(desired);

        Assert.Contains(checks, c => c.Status == TestStatus.Fail && c.Name == "IPv6 地址");
    }

    [Fact]
    public void ConnectWirelessWithoutProfileFails()
    {
        var desired = DesiredState.Dhcp() with
        {
            Wireless = new WirelessDesired(WirelessAction.Connect, "  ")
        };

        var checks = DesiredStateValidator.Validate(desired);

        Assert.Contains(checks, c => c.Status == TestStatus.Fail && c.Name == "Wi-Fi Profile");
    }

    [Fact]
    public void DhcpWithStaticAddressesOnlyWarns()
    {
        var desired = DesiredState.Dhcp() with
        {
            Ipv4 = Ipv4Desired.Dhcp() with
            {
                Addresses = new[] { new Ipv4Address("192.168.1.10", 24) }
            }
        };

        var checks = DesiredStateValidator.Validate(desired);

        Assert.Contains(checks, c => c.Status == TestStatus.Warn && c.Name == "IPv4 地址");
        Assert.True(DesiredStateValidator.CanApply(checks));
    }

    private static DesiredState StaticDesired() =>
        new(
            new Ipv4Desired(
                Ipv4Mode.Static,
                new[] { new Ipv4Address("192.168.11.50", 24) },
                new[] { new Gateway("192.168.11.1", 10) },
                DnsMode.Static,
                new[] { "223.5.5.5", "119.29.29.29" },
                MetricMode.Manual,
                20),
            Ipv6Desired.Keep(),
            WirelessDesired.Keep());
}

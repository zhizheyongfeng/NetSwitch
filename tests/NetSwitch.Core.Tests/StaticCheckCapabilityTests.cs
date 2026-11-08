using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;
using NetSwitch.Platform.Fake;
using Xunit;

namespace NetSwitch.Core.Tests;

public sealed class StaticCheckCapabilityTests
{
    [Fact]
    public async Task UnsupportedWirelessProfileIsBlocked()
    {
        var (checker, adapter) = await CreateAsync(capabilities =>
            capabilities.SupportsWirelessProfiles = false);

        var desired = DesiredState.Dhcp() with
        {
            Wireless = new WirelessDesired(WirelessAction.Connect, "家庭 Wi-Fi")
        };

        var report = await checker.ValidateAsync(adapter.Key, desired);

        Assert.False(report.CanApply);
        Assert.Contains(report.Checks, c => c.Name == "Wi-Fi 能力" && c.Status == TestStatus.Fail);
    }

    [Fact]
    public async Task UnsupportedIpv6AutoIsBlocked()
    {
        var (checker, adapter) = await CreateAsync(capabilities =>
            capabilities.SupportsIpv6BindingToggle = false);

        var desired = DesiredState.Dhcp() with { Ipv6 = Ipv6Desired.Auto() };

        var report = await checker.ValidateAsync(adapter.Key, desired);

        Assert.False(report.CanApply);
        Assert.Contains(report.Checks, c => c.Name == "IPv6 配置能力" && c.Status == TestStatus.Fail);
    }

    [Fact]
    public async Task UnsupportedIpv6DisabledIsBlocked()
    {
        var (checker, adapter) = await CreateAsync(capabilities =>
            capabilities.SupportsIpv6BindingToggle = false);

        var desired = DesiredState.Dhcp() with
        {
            Ipv6 = Ipv6Desired.Keep() with { Mode = Ipv6Mode.Disabled }
        };

        var report = await checker.ValidateAsync(adapter.Key, desired);

        Assert.False(report.CanApply);
        Assert.Contains(report.Checks, c => c.Name == "IPv6 绑定能力" && c.Status == TestStatus.Fail);
    }

    [Fact]
    public async Task KeepingIpv6AndWirelessPassesCapabilityChecks()
    {
        var (checker, adapter) = await CreateAsync(capabilities =>
        {
            capabilities.SupportsIpv6BindingToggle = false;
            capabilities.SupportsWirelessProfiles = false;
        });

        var report = await checker.ValidateAsync(adapter.Key, DesiredState.Dhcp());

        Assert.DoesNotContain(report.Checks, c =>
            c.Name.Contains("IPv6", StringComparison.Ordinal) && c.Status == TestStatus.Fail);
        Assert.DoesNotContain(report.Checks, c =>
            c.Name == "Wi-Fi 能力" && c.Status == TestStatus.Fail);
    }

    private static async Task<(StaticCheckService Checker, AdapterInfo Adapter)> CreateAsync(
        Action<FakePlatformCapabilities> configure)
    {
        var state = FakeNetworkState.CreateDefault();
        var catalog = new FakeAdapterCatalog(state);
        var adapter = (await catalog.GetAdaptersAsync()).First(a => a.Kind == AdapterKind.Ethernet);
        var capabilities = new FakePlatformCapabilities();
        configure(capabilities);

        var checker = new StaticCheckService(
            catalog,
            new FakeWirelessController(state),
            new FakePrivilegeService(),
            capabilities,
            new FakeConnectivityProbe { Delay = TimeSpan.Zero });

        return (checker, adapter);
    }
}

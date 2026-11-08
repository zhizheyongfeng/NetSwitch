using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;
using NetSwitch.Platform.Fake;
using Xunit;

namespace NetSwitch.Core.Tests;

public sealed class FakeBackendTests
{
    private static readonly DesiredState StaticDesired = new(
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

    [Fact]
    public async Task DefaultStateContainsPhysicalAndVirtualAdapters()
    {
        var state = FakeNetworkState.CreateDefault();
        var catalog = new FakeAdapterCatalog(state);

        var adapters = await catalog.GetAdaptersAsync();

        Assert.Equal(3, adapters.Count);
        Assert.Contains(adapters, a => a.Kind == AdapterKind.Ethernet && a.IsUp);
        Assert.Contains(adapters, a => a.Kind == AdapterKind.WiFi && a.IsUp);
        Assert.Contains(adapters, a => !a.IsUp);
    }

    [Fact]
    public async Task ApplyChangesStateAndRestoreRevertsIt()
    {
        var state = FakeNetworkState.CreateDefault();
        var catalog = new FakeAdapterCatalog(state);
        var adapter = (await catalog.GetAdaptersAsync()).First(a => a.Kind == AdapterKind.Ethernet);
        var before = await catalog.GetStateAsync(adapter.Key);
        Assert.NotNull(before);

        var configurator = new FakeNetworkConfigurator(state)
        {
            ApplyDelay = TimeSpan.Zero,
            RestoreDelay = TimeSpan.Zero
        };

        var applied = await configurator.ApplyAsync(
            adapter.Key,
            StaticDesired,
            ApplyOptions.Default);

        Assert.True(applied.Success);
        var after = await catalog.GetStateAsync(adapter.Key);
        Assert.NotNull(after);
        Assert.False(after!.Ipv4.DhcpEnabled);
        Assert.Equal("192.168.2.100", Assert.Single(after.Ipv4.Addresses).Address);
        Assert.Equal(10, Assert.Single(after.Ipv4.Gateways).Metric);
        Assert.Equal(DnsMode.Static, after.Ipv4.DnsMode);
        Assert.Equal(new[] { "192.168.2.1", "223.5.5.5" }, after.Ipv4.DnsServers);

        var restored = await configurator.RestoreAsync(
            adapter.Key,
            before!,
            ApplyOptions.Default);

        Assert.True(restored.Success);
        var current = await catalog.GetStateAsync(adapter.Key);
        Assert.NotNull(current);
        Assert.True(current!.Ipv4.DhcpEnabled);
        Assert.Equal(before!.Ipv4.Addresses, current.Ipv4.Addresses);
        Assert.Equal(before.Ipv4.Gateways, current.Ipv4.Gateways);
        Assert.Equal(before.Ipv4.DnsServers, current.Ipv4.DnsServers);
    }

    [Fact]
    public async Task FailedApplyLeavesStateUntouched()
    {
        var state = FakeNetworkState.CreateDefault();
        var catalog = new FakeAdapterCatalog(state);
        var adapter = (await catalog.GetAdaptersAsync()).First();
        var before = await catalog.GetStateAsync(adapter.Key);

        var configurator = new FakeNetworkConfigurator(state)
        {
            ApplyDelay = TimeSpan.Zero,
            FailNextApply = true
        };

        var result = await configurator.ApplyAsync(adapter.Key, StaticDesired, ApplyOptions.Default);

        Assert.False(result.Success);
        Assert.Equal(ApplyErrorCode.ApplyFailed, result.ErrorCode);
        var after = await catalog.GetStateAsync(adapter.Key);
        Assert.Equal(before, after);
    }
}

public sealed class PresetTesterTests
{
    private static readonly DesiredState StaticDesired = new(
        new Ipv4Desired(
            Ipv4Mode.Static,
            new[] { new Ipv4Address("192.168.11.50", 24) },
            new[] { new Gateway("192.168.11.1", 10) },
            DnsMode.Static,
            new[] { "223.5.5.5", "119.29.29.29" },
            MetricMode.Automatic,
            null),
        Ipv6Desired.Keep(),
        WirelessDesired.Keep());

    [Fact]
    public async Task TrialAppliesChecksAndRestoresOriginalState()
    {
        var fixture = CreateFixture(out var state, out var catalog);
        var adapter = (await catalog.GetAdaptersAsync()).First(a => a.Kind == AdapterKind.Ethernet);
        var before = await catalog.GetStateAsync(adapter.Key);

        var report = await fixture.Tester.RunTrialAsync(adapter.Key, StaticDesired, TestOptions.Default);

        Assert.True(report.TrialApplied);
        Assert.True(report.Restored);
        Assert.NotEqual(TestOverallStatus.RestoreFailed, report.Overall);
        Assert.NotEqual(TestOverallStatus.Aborted, report.Overall);
        Assert.Contains(
            report.Checks,
            c => c.Name == "恢复原配置" && c.Status == TestStatus.Pass);

        var after = await catalog.GetStateAsync(adapter.Key);
        Assert.Equal(before!.Ipv4.Addresses, after!.Ipv4.Addresses);
        Assert.Equal(before.Ipv4.Gateways, after.Ipv4.Gateways);
        Assert.Equal(before.Ipv4.DnsServers, after.Ipv4.DnsServers);
        Assert.Equal(before.Ipv4.DhcpEnabled, after.Ipv4.DhcpEnabled);
    }

    [Fact]
    public async Task TrialReportsGatewayFailureAndStillRestores()
    {
        var fixture = CreateFixture(out _, out var catalog);
        var adapter = (await catalog.GetAdaptersAsync()).First(a => a.Kind == AdapterKind.Ethernet);

        var desired = StaticDesired with
        {
            Ipv4 = StaticDesired.Ipv4 with
            {
                Gateways = new[] { new Gateway("192.168.99.1", 10) }
            }
        };

        var report = await fixture.Tester.RunTrialAsync(adapter.Key, desired, TestOptions.Default);

        Assert.True(report.TrialApplied);
        Assert.True(report.Restored);
        Assert.Equal(TestOverallStatus.Fail, report.Overall);
        Assert.Contains(report.Checks, c => c.Name == "网关连通性" && c.Status == TestStatus.Fail);
    }

    [Fact]
    public async Task FailedTrialApplyAbortsAndSkipsRestore()
    {
        var fixture = CreateFixture(out _, out var catalog);
        var adapter = (await catalog.GetAdaptersAsync()).First(a => a.Kind == AdapterKind.Ethernet);
        fixture.Configurator.FailNextApply = true;

        var report = await fixture.Tester.RunTrialAsync(adapter.Key, StaticDesired, TestOptions.Default);

        Assert.Equal(TestOverallStatus.Aborted, report.Overall);
        Assert.False(report.TrialApplied);
        Assert.True(report.Restored);
        Assert.Contains(report.Checks, c => c.Name == "临时应用" && c.Status == TestStatus.Fail);
        Assert.Contains(report.Checks, c => c.Name == "恢复原配置" && c.Status == TestStatus.Skipped);
    }

    [Fact]
    public async Task StaticCheckRequiresElevation()
    {
        var state = FakeNetworkState.CreateDefault();
        var catalog = new FakeAdapterCatalog(state);
        var adapter = (await catalog.GetAdaptersAsync()).First();
        var checker = new StaticCheckService(
            catalog,
            new FakeWirelessController(state),
            new FakePrivilegeService(isElevated: false),
            new FakePlatformCapabilities(),
            new FakeConnectivityProbe { Delay = TimeSpan.Zero });

        var report = await checker.ValidateAsync(adapter.Key, StaticDesired);

        Assert.False(report.CanApply);
        Assert.Contains(report.Checks, c => c.Name == "管理员权限" && c.Status == TestStatus.Fail);
    }

    [Fact]
    public async Task StaticCheckRejectsMissingWirelessProfile()
    {
        var state = FakeNetworkState.CreateDefault();
        var catalog = new FakeAdapterCatalog(state);
        var wifi = (await catalog.GetAdaptersAsync()).First(a => a.Kind == AdapterKind.WiFi);
        var checker = new StaticCheckService(
            catalog,
            new FakeWirelessController(state),
            new FakePrivilegeService(),
            new FakePlatformCapabilities(),
            new FakeConnectivityProbe { Delay = TimeSpan.Zero });

        var desired = DesiredState.Dhcp() with
        {
            Wireless = new WirelessDesired(WirelessAction.Connect, "不存在的网络")
        };

        var report = await checker.ValidateAsync(wifi.Key, desired);

        Assert.False(report.CanApply);
        Assert.Contains(report.Checks, c => c.Name == "Wi-Fi Profile" && c.Status == TestStatus.Fail);
    }

    private static Fixture CreateFixture(
        out FakeNetworkState state,
        out FakeAdapterCatalog catalog)
    {
        state = FakeNetworkState.CreateDefault();
        catalog = new FakeAdapterCatalog(state);
        var configurator = new FakeNetworkConfigurator(state)
        {
            ApplyDelay = TimeSpan.Zero,
            RestoreDelay = TimeSpan.Zero
        };
        var checker = new StaticCheckService(
            catalog,
            new FakeWirelessController(state),
            new FakePrivilegeService(),
            new FakePlatformCapabilities(),
            new FakeConnectivityProbe { Delay = TimeSpan.Zero });
        var tester = new PresetTester(
            checker,
            catalog,
            configurator,
            new FakeConnectivityProbe { Delay = TimeSpan.Zero });
        return new Fixture(tester, configurator);
    }

    private sealed record Fixture(
        PresetTester Tester,
        FakeNetworkConfigurator Configurator);
}

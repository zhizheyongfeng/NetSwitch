using NetSwitch.Core.Models;
using NetSwitch.Platform.Fake;
using Xunit;

namespace NetSwitch.Core.Tests;

public sealed class FakeRecoveryTests
{
    private static readonly DesiredState StaticDesired = new(
        new Ipv4Desired(
            Ipv4Mode.Static,
            new[] { new Ipv4Address("192.168.2.100", 24) },
            new[] { new Gateway("192.168.2.1", 10) },
            DnsMode.Static,
            new[] { "192.168.2.1" },
            MetricMode.Automatic,
            null),
        Ipv6Desired.Keep(),
        WirelessDesired.Keep());

    [Fact]
    public async Task SuccessfulApplyCanBeRestoredThroughRecoveryInfo()
    {
        var state = FakeNetworkState.CreateDefault();
        var catalog = new FakeAdapterCatalog(state);
        var configurator = FastConfigurator(state);
        var adapter = (await catalog.GetAdaptersAsync()).First(a => a.Kind == AdapterKind.Ethernet);
        var before = await catalog.GetStateAsync(adapter.Key);

        var applied = await configurator.ApplyAsync(adapter.Key, StaticDesired, ApplyOptions.Default);
        Assert.True(applied.Success);

        var info = await configurator.GetRecoveryInfoAsync();
        Assert.False(info.HasPending);
        Assert.True(info.HasLast);
        Assert.Equal(adapter.Key.FriendlyName, info.LastAdapterName);
        Assert.NotNull(info.LastAppliedAt);

        var restored = await configurator.RestoreLastAsync();
        Assert.True(restored.Success);

        var current = await catalog.GetStateAsync(adapter.Key);
        Assert.NotNull(current);
        Assert.True(current!.Ipv4.DhcpEnabled);
        Assert.Equal(before!.Ipv4.Addresses, current.Ipv4.Addresses);
        Assert.Equal(before.Ipv4.DnsServers, current.Ipv4.DnsServers);
    }

    [Fact]
    public async Task TrialKindDoesNotBecomeLastApplied()
    {
        var state = FakeNetworkState.CreateDefault();
        var catalog = new FakeAdapterCatalog(state);
        var configurator = FastConfigurator(state);
        var adapter = (await catalog.GetAdaptersAsync()).First(a => a.Kind == AdapterKind.Ethernet);

        var options = ApplyOptions.Default with { Kind = TransactionKind.Test };
        var applied = await configurator.ApplyAsync(adapter.Key, StaticDesired, options);
        Assert.True(applied.Success);

        var info = await configurator.GetRecoveryInfoAsync();
        Assert.False(info.HasLast);
        Assert.Null(info.LastAdapterName);
    }

    [Fact]
    public async Task FailedApplyDoesNotBecomeLastApplied()
    {
        var state = FakeNetworkState.CreateDefault();
        var configurator = FastConfigurator(state);
        configurator.FailNextApply = true;
        var adapter = (await new FakeAdapterCatalog(state).GetAdaptersAsync())
            .First(a => a.Kind == AdapterKind.Ethernet);

        var applied = await configurator.ApplyAsync(adapter.Key, StaticDesired, ApplyOptions.Default);

        Assert.False(applied.Success);
        var info = await configurator.GetRecoveryInfoAsync();
        Assert.False(info.HasLast);
    }

    [Fact]
    public async Task RestoreLastWithoutAnyApplyFails()
    {
        var configurator = FastConfigurator(FakeNetworkState.CreateDefault());

        var restored = await configurator.RestoreLastAsync();

        Assert.False(restored.Success);
        Assert.Equal(ApplyErrorCode.SnapshotFailed, restored.ErrorCode);
    }

    [Fact]
    public async Task RecoverPendingReportsNothingToDo()
    {
        var configurator = FastConfigurator(FakeNetworkState.CreateDefault());

        var result = await configurator.RecoverPendingAsync();

        Assert.True(result.Success);
        Assert.Contains("没有", result.Message);
    }

    private static FakeNetworkConfigurator FastConfigurator(FakeNetworkState state) =>
        new(state)
        {
            ApplyDelay = TimeSpan.Zero,
            RestoreDelay = TimeSpan.Zero
        };
}

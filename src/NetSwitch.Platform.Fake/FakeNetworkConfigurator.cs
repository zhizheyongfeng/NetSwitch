using System.Diagnostics;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Platform.Fake;

/// <summary>
/// Applies a desired state to the in-memory model. The shape of the result mirrors what
/// the Windows backend will return so the UI transaction code can be exercised in M1.
/// </summary>
public sealed class FakeNetworkConfigurator : INetworkConfigurator
{
    private readonly FakeNetworkState _state;
    private readonly object _recoverySync = new();
    private int _failNextApply;
    private AdapterKey? _lastKey;
    private AdapterState? _lastSnapshot;
    private DateTimeOffset? _lastAppliedAt;

    public FakeNetworkConfigurator(FakeNetworkState state)
    {
        _state = state;
    }

    public TimeSpan ApplyDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan RestoreDelay { get; set; } = TimeSpan.FromMilliseconds(120);

    /// <summary>When true the next apply fails after capturing the "before" state.</summary>
    public bool FailNextApply
    {
        get => Volatile.Read(ref _failNextApply) != 0;
        set => Interlocked.Exchange(ref _failNextApply, value ? 1 : 0);
    }

    public async Task<ApplyResult> ApplyAsync(
        AdapterKey key,
        DesiredState desired,
        ApplyOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(ApplyDelay, ct).ConfigureAwait(false);

        var before = _state.GetState(key);
        if (before is null)
        {
            return ApplyResult.Fail(
                ApplyErrorCode.AdapterNotFound,
                $"找不到适配器：{key.FriendlyName}（{key.StableId}）。",
                duration: stopwatch.Elapsed);
        }

        if (Interlocked.Exchange(ref _failNextApply, 0) != 0)
        {
            return ApplyResult.Fail(
                ApplyErrorCode.ApplyFailed,
                "模拟的应用失败（FakeNetworkConfigurator.FailNextApply）。",
                before,
                before,
                duration: stopwatch.Elapsed);
        }

        var after = BuildAfterState(key, before, desired);
        _state.SetState(after);

        if (options.Kind == TransactionKind.Apply)
        {
            lock (_recoverySync)
            {
                _lastKey = key;
                _lastSnapshot = before;
                _lastAppliedAt = DateTimeOffset.Now;
            }
        }

        return ApplyResult.Ok(
            $"已应用「{key.FriendlyName}」的配置（模拟后端）。",
            before,
            after,
            snapshotPath: null,
            stopwatch.Elapsed);
    }

    public async Task<ApplyResult> RestoreAsync(
        AdapterKey key,
        AdapterState snapshot,
        ApplyOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(RestoreDelay, ct).ConfigureAwait(false);

        var before = _state.GetState(key);
        var restored = snapshot with { CapturedAt = DateTimeOffset.Now };
        _state.SetState(restored);

        return ApplyResult.Ok(
            $"已恢复「{key.FriendlyName}」到应用前的状态（模拟后端）。",
            before,
            restored,
            snapshotPath: null,
            stopwatch.Elapsed);
    }

    public Task<RecoveryInfo> GetRecoveryInfoAsync(CancellationToken ct = default)
    {
        lock (_recoverySync)
        {
            return Task.FromResult(_lastSnapshot is null
                ? RecoveryInfo.None
                : new RecoveryInfo(
                    HasPending: false,
                    PendingKind: null,
                    PendingAdapterName: null,
                    PendingStartedAt: null,
                    HasLast: true,
                    LastAdapterName: _lastKey?.FriendlyName,
                    LastAppliedAt: _lastAppliedAt));
        }
    }

    public Task<ApplyResult> RecoverPendingAsync(CancellationToken ct = default)
    {
        // The fake backend has no crash recovery; pending transactions never exist.
        return Task.FromResult(ApplyResult.Ok(
            "没有未完成的事务。",
            null,
            null,
            null,
            TimeSpan.Zero));
    }

    public Task<ApplyResult> RestoreLastAsync(CancellationToken ct = default)
    {
        AdapterKey key;
        AdapterState snapshot;
        lock (_recoverySync)
        {
            if (_lastSnapshot is null || _lastKey is null)
            {
                return Task.FromResult(ApplyResult.Fail(
                    ApplyErrorCode.SnapshotFailed,
                    "没有可恢复的上次配置快照。"));
            }

            key = _lastKey;
            snapshot = _lastSnapshot;
        }

        return RestoreAsync(key, snapshot, ApplyOptions.Default, ct);
    }

    private static AdapterState BuildAfterState(
        AdapterKey key,
        AdapterState before,
        DesiredState desired)
    {
        var ipv4 = BuildIpv4(key, before.Ipv4, desired.Ipv4);
        var ipv6 = BuildIpv6(before.Ipv6, desired.Ipv6);
        var wireless = BuildWireless(before.Wireless, desired.Wireless);

        return before with
        {
            CapturedAt = DateTimeOffset.Now,
            Ipv4 = ipv4,
            Ipv6 = ipv6,
            Wireless = wireless
        };
    }

    private static Ipv4State BuildIpv4(AdapterKey key, Ipv4State current, Ipv4Desired desired)
    {
        var (dhcpAddress, gateway) = key.StableId.ToLowerInvariant() switch
        {
            "fake-wifi" => ("192.168.1.88", "192.168.1.1"),
            "fake-ethernet-2" => ("192.168.2.50", "192.168.2.1"),
            _ => ("192.168.1.23", "192.168.1.1")
        };

        var dhcpEnabled = desired.Mode == Ipv4Mode.Dhcp;
        var addresses = dhcpEnabled
            ? new[] { new Ipv4Address(dhcpAddress, 24) }
            : desired.Addresses.ToArray();

        var gateways = dhcpEnabled
            ? new[] { new Gateway(gateway) }
            : desired.Gateways.ToArray();

        var dnsMode = desired.DnsMode;
        var dnsServers = dnsMode == DnsMode.Static
            ? desired.DnsServers.ToArray()
            : new[] { dhcpEnabled ? gateway : current.DnsServers.FirstOrDefault() ?? "192.168.1.1" };

        var automaticMetric = desired.MetricMode switch
        {
            MetricMode.Automatic => true,
            MetricMode.Manual => false,
            _ => current.AutomaticMetric
        };
        var metric = desired.MetricMode == MetricMode.Manual
            ? desired.InterfaceMetric ?? 25
            : current.InterfaceMetric;

        return new Ipv4State(
            dhcpEnabled,
            addresses,
            gateways,
            dnsMode,
            dnsServers,
            automaticMetric,
            metric);
    }

    private static Ipv6State BuildIpv6(Ipv6State current, Ipv6Desired? desired)
    {
        if (desired is null || desired.Mode == Ipv6Mode.Keep)
        {
            return current;
        }

        if (desired.Mode == Ipv6Mode.Disabled)
        {
            return current with
            {
                BindingEnabled = false,
                Addresses = Array.Empty<Ipv4Address>(),
                Gateways = Array.Empty<Gateway>(),
                DnsServers = Array.Empty<string>(),
                DnsMode = DnsMode.Auto,
                Mode = Ipv6Mode.Disabled
            };
        }

        var automaticMetric = desired.MetricMode switch
        {
            MetricMode.Automatic => true,
            MetricMode.Manual => false,
            _ => current.AutomaticMetric
        };
        var metric = desired.MetricMode == MetricMode.Manual
            ? desired.InterfaceMetric ?? 25
            : current.InterfaceMetric;

        if (desired.Mode == Ipv6Mode.Auto)
        {
            var hadStaticAddresses = current.Mode == Ipv6Mode.Static;
            return current with
            {
                BindingEnabled = true,
                Addresses = hadStaticAddresses ? Array.Empty<Ipv4Address>() : current.Addresses,
                Gateways = hadStaticAddresses ? Array.Empty<Gateway>() : current.Gateways,
                DnsServers = desired.DnsMode == DnsMode.Static
                    ? desired.DnsServers.ToArray()
                    : Array.Empty<string>(),
                DnsMode = desired.DnsMode,
                Mode = Ipv6Mode.Auto,
                AutomaticMetric = automaticMetric,
                InterfaceMetric = metric
            };
        }

        return new Ipv6State(
            BindingEnabled: true,
            Addresses: desired.Addresses.ToArray(),
            Gateways: desired.Gateways.ToArray(),
            DnsServers: desired.DnsMode == DnsMode.Static
                ? desired.DnsServers.ToArray()
                : Array.Empty<string>(),
            AutomaticMetric: automaticMetric,
            InterfaceMetric: metric,
            DnsMode: desired.DnsMode,
            Mode: Ipv6Mode.Static);
    }

    private static WirelessState? BuildWireless(WirelessState? current, WirelessDesired? desired)
    {
        if (desired is null || desired.Action == WirelessAction.Keep)
        {
            return current;
        }

        return desired.Action switch
        {
            WirelessAction.Connect => new WirelessState(true, desired.SavedProfileName, desired.SavedProfileName),
            WirelessAction.Disconnect => new WirelessState(false, null, null),
            _ => current
        };
    }
}

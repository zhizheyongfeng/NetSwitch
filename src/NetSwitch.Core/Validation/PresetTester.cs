using System.Diagnostics;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Core.Validation;

public sealed class PresetTester : IPresetTester
{
    private readonly IStaticChecker _staticChecker;
    private readonly IAdapterCatalog _catalog;
    private readonly INetworkConfigurator _configurator;
    private readonly IConnectivityProbe _probe;

    public PresetTester(
        IStaticChecker staticChecker,
        IAdapterCatalog catalog,
        INetworkConfigurator configurator,
        IConnectivityProbe probe)
    {
        _staticChecker = staticChecker;
        _catalog = catalog;
        _configurator = configurator;
        _probe = probe;
    }

    public Task<StaticCheckReport> ValidateAsync(
        AdapterKey key,
        DesiredState desired,
        CancellationToken ct = default) =>
        _staticChecker.ValidateAsync(key, desired, ct);

    public async Task<ConnectivityTestReport> RunTrialAsync(
        AdapterKey key,
        DesiredState desired,
        TestOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var before = await _catalog.GetStateAsync(key, ct).ConfigureAwait(false);
        if (before is null)
        {
            return new ConnectivityTestReport(
                TestOverallStatus.Aborted,
                TrialApplied: false,
                Restored: true,
                new[] { TestCheck.Fail("适配器状态", "无法读取应用前的状态，已取消试运行。") },
                stopwatch.Elapsed,
                SnapshotPath: null);
        }

        var applyOptions = new ApplyOptions(
            AutoRevertMode: AutoRevertMode.Off,
            AutoRevertSeconds: options.TrialMaxSeconds,
            ConfirmOnFailure: false,
            TransactionId: Guid.NewGuid().ToString("N"),
            Kind: TransactionKind.Test);

        var apply = await _configurator.ApplyAsync(key, desired, applyOptions, ct).ConfigureAwait(false);
        if (!apply.Success)
        {
            return new ConnectivityTestReport(
                TestOverallStatus.Aborted,
                TrialApplied: false,
                Restored: true,
                new[]
                {
                    TestCheck.Fail("临时应用", apply.Message),
                    TestCheck.Skip("恢复原配置", "配置未生效，无需恢复。")
                },
                stopwatch.Elapsed,
                apply.SnapshotPath);
        }

        var checks = new List<TestCheck>();
        var restored = false;
        try
        {
            checks.AddRange(await RunChecksAsync(key, desired, apply.After, options, ct)
                .ConfigureAwait(false));
        }
        finally
        {
            try
            {
                var restore = await _configurator
                    .RestoreAsync(key, before, applyOptions, CancellationToken.None)
                    .ConfigureAwait(false);
                restored = restore.Success;
                checks.Add(restored
                    ? TestCheck.Pass("恢复原配置", "已恢复到应用前的网络配置。")
                    : TestCheck.Fail("恢复原配置", $"恢复失败：{restore.Message}"));
            }
            catch (Exception ex)
            {
                checks.Add(TestCheck.Fail("恢复原配置", $"恢复失败：{ex.Message}"));
            }
        }

        var overall = !restored
            ? TestOverallStatus.RestoreFailed
            : checks.Any(c => c.Status == TestStatus.Fail)
                ? TestOverallStatus.Fail
                : checks.Any(c => c.Status == TestStatus.Warn)
                    ? TestOverallStatus.Warn
                    : TestOverallStatus.Pass;

        return new ConnectivityTestReport(
            overall,
            TrialApplied: true,
            Restored: restored,
            checks,
            stopwatch.Elapsed,
            apply.SnapshotPath);
    }

    private async Task<IReadOnlyList<TestCheck>> RunChecksAsync(
        AdapterKey key,
        DesiredState desired,
        AdapterState? after,
        TestOptions options,
        CancellationToken ct)
    {
        var checks = new List<TestCheck>();
        var adapter = await _catalog.GetAdapterAsync(key, ct).ConfigureAwait(false);
        if (adapter is not { IsUp: true })
        {
            checks.Add(TestCheck.Warn("适配器状态", "适配器当前未连接，无法进行连通性检查。"));
            return checks;
        }

        var applied = after ?? await _catalog.GetStateAsync(key, ct).ConfigureAwait(false);
        var ipv4 = applied?.Ipv4;

        if (options.PingGateway)
        {
            var gateways = ipv4?.Gateways ?? desired.Ipv4.Gateways;
            var gateway = gateways.FirstOrDefault();
            if (gateway is null)
            {
                checks.Add(TestCheck.Skip("网关连通性", "当前配置没有默认网关（例如仅本地网络）。"));
            }
            else
            {
                var ping = await _probe.PingAsync(gateway.Address, options.TimeoutSeconds * 1000, ct)
                    .ConfigureAwait(false);
                checks.Add(ping.Success
                    ? TestCheck.Pass("网关连通性", $"可以访问网关 {gateway.Address}。", ping.Latency, gateway.Address)
                    : TestCheck.Fail("网关连通性", $"无法访问网关 {gateway.Address}：{ping.Detail}", ping.Latency, gateway.Address));
            }
        }

        if (options.TestDnsServersDirectly)
        {
            var servers = (ipv4?.DnsServers ?? desired.Ipv4.DnsServers)
                .Where(s => System.Net.IPAddress.TryParse(s, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();

            if (servers.Length == 0)
            {
                checks.Add(TestCheck.Skip("DNS 服务器", "没有可直接查询的 DNS 服务器。"));
            }
            else
            {
                foreach (var server in servers)
                {
                    var query = await _probe
                        .QueryDnsAsync(server, options.DnsDomain, options.TimeoutSeconds * 1000, ct)
                        .ConfigureAwait(false);
                    checks.Add(query.Success
                        ? TestCheck.Pass("DNS 服务器", $"{server} 可以解析 {options.DnsDomain}。", query.Latency, server)
                        : TestCheck.Fail("DNS 服务器", $"{server} 查询失败：{query.Detail}", query.Latency, server));
                }
            }
        }

        var resolved = await _probe.ResolveAsync(options.DnsDomain, ct).ConfigureAwait(false);
        checks.Add(resolved.Success
            ? TestCheck.Pass("域名解析", $"{options.DnsDomain} 解析成功。", resolved.Latency, options.DnsDomain)
            : TestCheck.Fail("域名解析", $"{options.DnsDomain} 解析失败：{resolved.Detail}", resolved.Latency, options.DnsDomain));

        if (options.TestPublicTcp)
        {
            var tcp = await _probe
                .TcpConnectAsync(options.DnsDomain, options.TcpPort, options.TimeoutSeconds * 1000, ct)
                .ConfigureAwait(false);
            checks.Add(tcp.Success
                ? TestCheck.Pass("公网 TCP", $"可以连接 {options.DnsDomain}:{options.TcpPort}。", tcp.Latency, options.DnsDomain)
                : TestCheck.Fail("公网 TCP", $"无法连接 {options.DnsDomain}:{options.TcpPort}：{tcp.Detail}", tcp.Latency, options.DnsDomain));
        }

        return checks;
    }
}

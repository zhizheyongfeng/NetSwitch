using NetSwitch.Core.Abstractions;

namespace NetSwitch.Platform.Fake;

/// <summary>
/// Deterministic probe for demos and tests. Known gateways and public DNS servers respond;
/// arbitrary addresses do not, which keeps the static conflict precheck realistic.
/// </summary>
public sealed class FakeConnectivityProbe : IConnectivityProbe
{
    private readonly HashSet<string> _responding = new(StringComparer.OrdinalIgnoreCase)
    {
        "192.168.1.1",
        "192.168.2.1",
        "192.168.11.1",
        "223.5.5.5",
        "119.29.29.29",
        "8.8.8.8",
        "1.1.1.1"
    };

    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(15);

    public void SetResponding(string host, bool responds)
    {
        lock (_responding)
        {
            if (responds)
            {
                _failing.Remove(host);
                _responding.Add(host);
            }
            else
            {
                _responding.Remove(host);
                _failing.Add(host);
            }
        }
    }

    public async Task<ProbeResult> PingAsync(
        string host,
        int timeoutMilliseconds = 1000,
        CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);

        lock (_responding)
        {
            if (_failing.Contains(host))
            {
                return ProbeResult.Fail(Delay, "目标无应答（模拟）。");
            }

            return _responding.Contains(host)
                ? ProbeResult.Ok(TimeSpan.FromMilliseconds(2), "收到 ICMP 应答（模拟）。")
                : ProbeResult.Fail(TimeSpan.FromMilliseconds(Math.Max(timeoutMilliseconds, 1)), "目标无应答（模拟）。");
        }
    }

    public async Task<ProbeResult> ResolveAsync(string host, CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);

        lock (_responding)
        {
            return _failing.Contains(host)
                ? ProbeResult.Fail(Delay, "DNS 解析失败（模拟）。")
                : ProbeResult.Ok(TimeSpan.FromMilliseconds(14), "解析成功（模拟）。");
        }
    }

    public async Task<ProbeResult> QueryDnsAsync(
        string dnsServer,
        string host,
        int timeoutMilliseconds = 2000,
        CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);

        lock (_responding)
        {
            return _failing.Contains(dnsServer) || _failing.Contains(host)
                ? ProbeResult.Fail(Delay, "DNS 查询无响应（模拟）。")
                : ProbeResult.Ok(TimeSpan.FromMilliseconds(24), "DNS 查询成功（模拟）。");
        }
    }

    public async Task<ProbeResult> TcpConnectAsync(
        string host,
        int port,
        int timeoutMilliseconds = 3000,
        CancellationToken ct = default)
    {
        await Task.Delay(Delay, ct).ConfigureAwait(false);

        lock (_responding)
        {
            return _failing.Contains(host)
                ? ProbeResult.Fail(Delay, "TCP 连接失败（模拟）。")
                : ProbeResult.Ok(TimeSpan.FromMilliseconds(41), $"TCP {port} 连接成功（模拟）。");
        }
    }
}

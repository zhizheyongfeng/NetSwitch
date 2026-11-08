namespace NetSwitch.Core.Abstractions;

public sealed record ProbeResult(
    bool Success,
    TimeSpan Latency,
    string? Detail = null)
{
    public static ProbeResult Ok(TimeSpan latency, string? detail = null) => new(true, latency, detail);

    public static ProbeResult Fail(TimeSpan latency, string detail) => new(false, latency, detail);
}

public interface IConnectivityProbe
{
    Task<ProbeResult> PingAsync(
        string host,
        int timeoutMilliseconds = 1000,
        CancellationToken ct = default);

    Task<ProbeResult> ResolveAsync(
        string host,
        CancellationToken ct = default);

    Task<ProbeResult> QueryDnsAsync(
        string dnsServer,
        string host,
        int timeoutMilliseconds = 2000,
        CancellationToken ct = default);

    Task<ProbeResult> TcpConnectAsync(
        string host,
        int port,
        int timeoutMilliseconds = 3000,
        CancellationToken ct = default);
}

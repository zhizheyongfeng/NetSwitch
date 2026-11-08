using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using NetSwitch.Core.Abstractions;

namespace NetSwitch.Platform.Windows;

/// <summary>
/// Connectivity checks for the trial run. Ping and TCP use the .NET networking stack;
/// the direct DNS query is a minimal UDP A-record query sent to one specific server, which
/// proves that a preset's own DNS server answers without relying on the system resolver.
/// </summary>
public sealed class WindowsConnectivityProbe : IConnectivityProbe
{
    private const int DnsPort = 53;
    private const ushort QueryTypeA = 1;
    private const ushort QueryClassIn = 1;

    public async Task<ProbeResult> PingAsync(
        string host,
        int timeoutMilliseconds = 1000,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, Math.Max(1, timeoutMilliseconds))
                .WaitAsync(ct)
                .ConfigureAwait(false);

            stopwatch.Stop();
            return reply.Status == IPStatus.Success
                ? ProbeResult.Ok(stopwatch.Elapsed, $"往返 {reply.RoundtripTime} ms")
                : ProbeResult.Fail(stopwatch.Elapsed, reply.Status.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProbeResult.Fail(stopwatch.Elapsed, "超时");
        }
        catch (Exception ex)
        {
            return ProbeResult.Fail(stopwatch.Elapsed, ex.Message);
        }
    }

    public async Task<ProbeResult> ResolveAsync(string host, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            stopwatch.Stop();
            return addresses.Length > 0
                ? ProbeResult.Ok(
                    stopwatch.Elapsed,
                    string.Join(", ", addresses.Take(3).Select(a => a.ToString())))
                : ProbeResult.Fail(stopwatch.Elapsed, "没有返回地址");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProbeResult.Fail(stopwatch.Elapsed, "超时");
        }
        catch (Exception ex)
        {
            return ProbeResult.Fail(stopwatch.Elapsed, ex.Message);
        }
    }

    public async Task<ProbeResult> QueryDnsAsync(
        string dnsServer,
        string host,
        int timeoutMilliseconds = 2000,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!IPAddress.TryParse(dnsServer, out var server))
        {
            return ProbeResult.Fail(stopwatch.Elapsed, "DNS 服务器地址无效");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Max(1, timeoutMilliseconds));

        try
        {
            var id = (ushort)Random.Shared.Next(0, ushort.MaxValue + 1);
            var query = BuildQuery(host, id);

            using var udp = new UdpClient(server.AddressFamily);
            await udp.SendAsync(query.AsMemory(), new IPEndPoint(server, DnsPort), timeout.Token)
                .ConfigureAwait(false);
            var response = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);

            stopwatch.Stop();
            return EvaluateResponse(response.Buffer, id, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return ProbeResult.Fail(stopwatch.Elapsed, "查询超时");
        }
        catch (OperationCanceledException)
        {
            return ProbeResult.Fail(stopwatch.Elapsed, "已取消");
        }
        catch (SocketException ex)
        {
            return ProbeResult.Fail(stopwatch.Elapsed, $"网络错误：{ex.SocketErrorCode}");
        }
        catch (Exception ex)
        {
            return ProbeResult.Fail(stopwatch.Elapsed, ex.Message);
        }
    }

    public async Task<ProbeResult> TcpConnectAsync(
        string host,
        int port,
        int timeoutMilliseconds = 3000,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Max(1, timeoutMilliseconds));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return ProbeResult.Ok(stopwatch.Elapsed, client.Connected ? "已连接" : "连接已关闭");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return ProbeResult.Fail(stopwatch.Elapsed, "连接超时");
        }
        catch (OperationCanceledException)
        {
            return ProbeResult.Fail(stopwatch.Elapsed, "已取消");
        }
        catch (SocketException ex)
        {
            return ProbeResult.Fail(stopwatch.Elapsed, $"网络错误：{ex.SocketErrorCode}");
        }
        catch (Exception ex)
        {
            return ProbeResult.Fail(stopwatch.Elapsed, ex.Message);
        }
    }

    private static ProbeResult EvaluateResponse(
        byte[] response,
        ushort expectedId,
        TimeSpan latency)
    {
        if (response.Length < 12)
        {
            return ProbeResult.Fail(latency, "响应过短");
        }

        var id = (ushort)((response[0] << 8) | response[1]);
        var flags = (response[2] << 8) | response[3];
        var answerCount = (response[6] << 8) | response[7];
        var responseCode = flags & 0x000F;

        if (id != expectedId)
        {
            return ProbeResult.Fail(latency, "响应 ID 不匹配");
        }

        if ((flags & 0x8000) == 0)
        {
            return ProbeResult.Fail(latency, "响应不是 DNS 应答");
        }

        if (responseCode != 0)
        {
            return ProbeResult.Fail(latency, $"DNS 返回码 {responseCode}");
        }

        return answerCount > 0
            ? ProbeResult.Ok(latency, $"收到 {answerCount} 条应答")
            : ProbeResult.Fail(latency, "没有解析结果");
    }

    private static byte[] BuildQuery(string host, ushort id)
    {
        using var stream = new MemoryStream(64);
        WriteUInt16(stream, id);
        WriteUInt16(stream, 0x0100); // Standard query, recursion desired.
        WriteUInt16(stream, 1); // QDCOUNT
        WriteUInt16(stream, 0); // ANCOUNT
        WriteUInt16(stream, 0); // NSCOUNT
        WriteUInt16(stream, 0); // ARCOUNT

        var ascii = new IdnMapping().GetAscii(host.TrimEnd('.'));
        foreach (var label in ascii.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new ArgumentException($"DNS 名称标签无效：{label}");
            }

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        WriteUInt16(stream, QueryTypeA);
        WriteUInt16(stream, QueryClassIn);
        return stream.ToArray();
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}

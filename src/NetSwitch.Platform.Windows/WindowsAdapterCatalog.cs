using System.Net;
using System.Net.Sockets;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;
using NetSwitch.Platform.Windows.Native;

namespace NetSwitch.Platform.Windows;

/// <summary>
/// Reads adapter inventory and live state through the IP Helper API, plus one read-only
/// registry lookup to tell automatic DNS apart from a static DNS list.
/// </summary>
public sealed class WindowsAdapterCatalog : IAdapterCatalog
{
    private const int MaxIpv6Addresses = 32;
    private readonly IWirelessController _wireless;

    public WindowsAdapterCatalog(IWirelessController wireless)
    {
        _wireless = wireless;
    }

    public Task<IReadOnlyList<AdapterInfo>> GetAdaptersAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<AdapterInfo>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureWindows();

            return IpHelperApi.GetAdapters()
                .Select(snapshot => snapshot.ToInfo())
                .OrderBy(info => SortRank(info.Kind))
                .ThenBy(info => info.Key.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }, ct);

    public Task<AdapterInfo?> GetAdapterAsync(AdapterKey key, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureWindows();

            var snapshot = Find(key);
            return snapshot?.ToInfo();
        }, ct);

    public async Task<AdapterState?> GetStateAsync(AdapterKey key, CancellationToken ct = default)
    {
        var snapshot = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureWindows();

            return Find(key);
        }, ct).ConfigureAwait(false);

        if (snapshot is null)
        {
            return null;
        }

        WirelessState? wireless = null;
        if (snapshot.Kind == AdapterKind.WiFi)
        {
            try
            {
                wireless = await _wireless.GetStateAsync(key, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A stopped WLAN service must not make the whole adapter unreadable.
                wireless = null;
            }
        }

        return BuildState(snapshot, wireless);
    }

    private static AdapterState BuildState(NativeAdapterSnapshot snapshot, WirelessState? wireless)
    {
        var staticDns4 = TcpipRegistry.ReadStaticDnsServers(snapshot.Key)
            ?.Where(server => IPAddress.TryParse(server, out var ip)
                              && ip.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();

        var staticDns6 = TcpipRegistry.ReadStaticDnsServers(snapshot.Key, ipv6: true)
            ?.Where(server => IPAddress.TryParse(server, out var ip)
                              && ip.AddressFamily == AddressFamily.InterNetworkV6)
            .ToArray();

        var effectiveDns4 = snapshot.DnsServers
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .Select(ip => ip.ToString())
            .ToArray();

        var effectiveDns6 = snapshot.DnsServers
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
            .Select(ip => ip.ToString())
            .ToArray();

        var dns4 = staticDns4 is { Length: > 0 } ? staticDns4 : effectiveDns4;
        var dns6 = staticDns6 is { Length: > 0 } ? staticDns6 : effectiveDns6;

        var metric4 = IpHelperApi.GetInterfaceMetric(
            snapshot.Luid,
            snapshot.InterfaceIndex,
            NativeConstants.AF_INET);
        var metric6 = IpHelperApi.GetInterfaceMetric(
            snapshot.Luid,
            snapshot.InterfaceIndex,
            NativeConstants.AF_INET6);

        var ipv4 = new Ipv4State(
            snapshot.DhcpEnabled,
            snapshot.Ipv4Addresses
                .Where(a => !a.Address.Equals(IPAddress.Any))
                .Select(a => new Ipv4Address(a.Address.ToString(), a.PrefixLength))
                .ToArray(),
            snapshot.Gateways
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .Distinct()
                .Select(ip => new Gateway(ip.ToString()))
                .ToArray(),
            staticDns4 is { Length: > 0 } ? DnsMode.Static : DnsMode.Auto,
            dns4,
            metric4?.UseAutomatic ?? true,
            metric4?.Metric ?? snapshot.Ipv4Metric);

        var ipv6Mode = !snapshot.Ipv6Enabled
            ? Ipv6Mode.Disabled
            : snapshot.Ipv6Addresses.Any(a =>
                Ipv6AddressUtil.IsGlobalUnicast(a.Address) &&
                a.PrefixOrigin == NativeConstants.IP_PREFIX_ORIGIN_MANUAL &&
                a.SuffixOrigin == NativeConstants.IP_SUFFIX_ORIGIN_MANUAL)
                ? Ipv6Mode.Static
                : Ipv6Mode.Auto;

        var ipv6 = new Ipv6State(
            snapshot.Ipv6Enabled,
            snapshot.Ipv6Addresses
                .Where(a => !a.Address.Equals(IPAddress.IPv6Any))
                .Select(a => new Ipv4Address(a.Address.ToString(), a.PrefixLength))
                .Take(MaxIpv6Addresses)
                .ToArray(),
            snapshot.Gateways
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
                .Distinct()
                .Select(ip => new Gateway(ip.ToString()))
                .ToArray(),
            dns6,
            metric6?.UseAutomatic ?? true,
            metric6?.Metric ?? snapshot.Ipv6Metric,
            staticDns6 is { Length: > 0 } ? DnsMode.Static : DnsMode.Auto,
            ipv6Mode);

        return new AdapterState(
            snapshot.Key,
            DateTimeOffset.Now,
            ipv4,
            ipv6,
            wireless);
    }

    private static NativeAdapterSnapshot? Find(AdapterKey key)
    {
        if (key.IsEmpty)
        {
            return null;
        }

        return IpHelperApi.GetAdapters().FirstOrDefault(snapshot =>
            string.Equals(snapshot.Key.StableId, key.StableId, StringComparison.OrdinalIgnoreCase));
    }

    private static int SortRank(AdapterKind kind) => kind switch
    {
        AdapterKind.WiFi => 0,
        AdapterKind.Ethernet => 1,
        AdapterKind.Other => 2,
        _ => 3
    };

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows 网络适配器后端只能在 Windows 上运行。");
        }
    }
}

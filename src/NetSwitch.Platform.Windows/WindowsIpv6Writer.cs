using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;
using NetSwitch.Platform.Windows.Native;

namespace NetSwitch.Platform.Windows;

/// <summary>
/// Writes the IPv6 part of a preset. Binding changes go through INetCfg and reset the
/// adapter, so the writer waits until the new binding state is visible before touching
/// addresses. Everything else uses the IP Helper API.
/// </summary>
internal sealed class WindowsIpv6Writer
{
    private static readonly TimeSpan BindingTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BindingPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IAdapterCatalog _catalog;

    public WindowsIpv6Writer(IAdapterCatalog catalog)
    {
        _catalog = catalog;
    }

    public async Task ApplyAsync(
        AdapterInfo adapter,
        Ipv6Desired desired,
        CancellationToken ct)
    {
        if (desired.Mode == Ipv6Mode.Keep)
        {
            return;
        }

        var bindingEnabled = desired.Mode != Ipv6Mode.Disabled;
        await EnsureBindingAsync(adapter, bindingEnabled, ct).ConfigureAwait(false);
        if (!bindingEnabled)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        await Task.Run(() => WriteEnabled(adapter, desired), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task EnsureBindingAsync(AdapterInfo adapter, bool enabled, CancellationToken ct)
    {
        var current = await TryReadStateAsync(adapter.Key, ct).ConfigureAwait(false);
        if (current?.Ipv6.BindingEnabled == enabled)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        await NetCfgApi.SetIpv6BindingAsync(adapter.Key.StableId, enabled).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + BindingTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(BindingPollInterval, ct).ConfigureAwait(false);

            current = await TryReadStateAsync(adapter.Key, ct).ConfigureAwait(false);
            if (current?.Ipv6.BindingEnabled == enabled)
            {
                return;
            }
        }

        throw new TimeoutException(
            $"IPv6 绑定未在 {BindingTimeout.TotalSeconds:0} 秒内{(enabled ? "启用" : "禁用")}。" +
            "适配器可能仍在重置，请稍后重试。");
    }

    private async Task<AdapterState?> TryReadStateAsync(AdapterKey key, CancellationToken ct)
    {
        try
        {
            return await _catalog.GetStateAsync(key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The adapter is expected to disappear briefly while the binding is applied.
            return null;
        }
    }

    private static void WriteEnabled(AdapterInfo adapter, Ipv6Desired desired)
    {
        if (desired.Mode == Ipv6Mode.Static)
        {
            // Stop RAs before deleting addresses, otherwise Windows can re-add an address
            // between the delete and the static write.
            if (!IpHelperApi.SetIpv6RouterDiscovery(adapter.Luid, adapter.InterfaceIndex, false, out var error))
            {
                throw new InvalidOperationException(error);
            }

            Ipv6NativeApi.DeleteGlobalUnicastAddresses(adapter.InterfaceIndex, manualOnly: false);
            Ipv6NativeApi.DeleteDefaultRoutes(adapter.InterfaceIndex, netMgmtOnly: false);

            foreach (var address in desired.Addresses)
            {
                Ipv6NativeApi.AddUnicastAddress(adapter.Luid, adapter.InterfaceIndex, address);
            }

            foreach (var gateway in desired.Gateways)
            {
                Ipv6NativeApi.AddDefaultRoute(adapter.Luid, adapter.InterfaceIndex, gateway);
            }
        }
        else
        {
            Ipv6NativeApi.DeleteDefaultRoutes(adapter.InterfaceIndex, netMgmtOnly: true);
            Ipv6NativeApi.DeleteGlobalUnicastAddresses(adapter.InterfaceIndex, manualOnly: true);

            if (!IpHelperApi.SetIpv6RouterDiscovery(adapter.Luid, adapter.InterfaceIndex, true, out var error))
            {
                throw new InvalidOperationException(error);
            }
        }

        Ipv6NativeApi.SetDns(
            adapter.Key.StableId,
            desired.DnsMode == DnsMode.Static ? desired.DnsServers : null);

        if (desired.MetricMode != MetricMode.Keep)
        {
            var automatic = desired.MetricMode == MetricMode.Automatic;
            if (!IpHelperApi.SetInterfaceMetric(
                    NativeConstants.AF_INET6,
                    adapter.Luid,
                    adapter.InterfaceIndex,
                    automatic,
                    desired.InterfaceMetric ?? 1,
                    out var metricError))
            {
                throw new InvalidOperationException(metricError);
            }
        }

        IpHelperApi.FlushDnsCache();
    }
}

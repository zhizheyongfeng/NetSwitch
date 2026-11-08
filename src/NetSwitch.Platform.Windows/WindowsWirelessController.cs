using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;
using NetSwitch.Platform.Windows.Native;

namespace NetSwitch.Platform.Windows;

/// <summary>
/// Wi-Fi profile switching through the WLAN API. Only profiles already saved by Windows
/// are used; the application never reads or stores the Wi-Fi key. WlanConnect is
/// asynchronous, so every operation waits for the interface state to settle.
/// </summary>
public sealed class WindowsWirelessController : IWirelessController
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    public Task<IReadOnlyList<string>> GetSavedProfilesAsync(
        AdapterKey key,
        CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureWindows();

            using var client = new WlanClient();
            var guid = client.RequireInterface(key.StableId);
            return client.GetProfileNames(guid).ToArray();
        }, ct);

    public Task<WirelessState?> GetStateAsync(
        AdapterKey key,
        CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureWindows();

            using var client = new WlanClient();
            var guid = client.RequireInterface(key.StableId);
            return (WirelessState?)ToState(client.GetConnection(guid));
        }, ct);

    public Task<WirelessState> ConnectAsync(
        AdapterKey key,
        string savedProfileName,
        CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureWindows();

            using var client = new WlanClient();
            var guid = client.RequireInterface(key.StableId);
            var profiles = client.GetProfileNames(guid);
            if (!profiles.Contains(savedProfileName, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"找不到已保存的 Wi-Fi Profile：{savedProfileName}。请先在 Windows 中连接一次该网络。");
            }

            var current = ToState(client.GetConnection(guid));
            if (current is { IsConnected: true } && ProfileMatches(current, savedProfileName))
            {
                return current;
            }

            client.Connect(guid, savedProfileName);

            var deadline = DateTime.UtcNow + ConnectTimeout;
            WirelessState? last = current;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                last = ToState(client.GetConnection(guid));

                if (last is { IsConnected: true } && ProfileMatches(last, savedProfileName))
                {
                    return last;
                }
            }

            var actual = last is { IsConnected: true }
                ? $"当前连接为「{last.ActiveProfileName ?? last.Ssid}」"
                : "接口仍未连接";
            throw new TimeoutException(
                $"未能在 {ConnectTimeout.TotalSeconds:0} 秒内连接到「{savedProfileName}」，{actual}。");
        }, ct);

    public Task<WirelessState> DisconnectAsync(
        AdapterKey key,
        CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureWindows();

            using var client = new WlanClient();
            var guid = client.RequireInterface(key.StableId);
            var current = ToState(client.GetConnection(guid));
            if (current is not { IsConnected: true })
            {
                return current;
            }

            client.Disconnect(guid);

            var deadline = DateTime.UtcNow + DisconnectTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                current = ToState(client.GetConnection(guid));
                if (current is not { IsConnected: true })
                {
                    return current;
                }
            }

            throw new TimeoutException(
                $"未能在 {DisconnectTimeout.TotalSeconds:0} 秒内断开 Wi-Fi（仍连接「{current.ActiveProfileName ?? current.Ssid}」）。");
        }, ct);

    private static WirelessState ToState(WlanNativeApi.WlanConnectionAttributes? attributes)
    {
        if (attributes is not { } info || info.State != WlanNativeApi.WlanInterfaceStateConnected)
        {
            return new WirelessState(false, null, null);
        }

        var ssidInfo = info.Association.Ssid;
        var ssid = WlanNativeApi.DecodeSsid(ssidInfo.Ssid, ssidInfo.Length);
        var profile = string.IsNullOrWhiteSpace(info.ProfileName)
            ? ssid
            : info.ProfileName.Trim();

        return new WirelessState(
            true,
            string.IsNullOrWhiteSpace(profile) ? null : profile,
            string.IsNullOrWhiteSpace(ssid) ? null : ssid);
    }

    private static bool ProfileMatches(WirelessState state, string profileName) =>
        string.Equals(state.ActiveProfileName, profileName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state.Ssid, profileName, StringComparison.OrdinalIgnoreCase);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Wi-Fi 后端只能在 Windows 上运行。");
        }
    }
}

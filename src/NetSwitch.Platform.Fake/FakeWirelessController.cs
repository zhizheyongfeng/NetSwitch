using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Platform.Fake;

public sealed class FakeWirelessController : IWirelessController
{
    private readonly FakeNetworkState _state;

    public FakeWirelessController(FakeNetworkState state)
    {
        _state = state;
    }

    public TimeSpan ConnectDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    public Task<IReadOnlyList<string>> GetSavedProfilesAsync(AdapterKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_state.GetProfiles(key));
    }

    public Task<WirelessState?> GetStateAsync(AdapterKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_state.GetState(key)?.Wireless);
    }

    public async Task<WirelessState> ConnectAsync(
        AdapterKey key,
        string savedProfileName,
        CancellationToken ct = default)
    {
        await Task.Delay(ConnectDelay, ct).ConfigureAwait(false);

        var profiles = _state.GetProfiles(key);
        if (!profiles.Contains(savedProfileName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"找不到已保存的 Wi-Fi Profile：{savedProfileName}");
        }

        var current = _state.GetState(key)
            ?? throw new InvalidOperationException($"找不到适配器：{key.FriendlyName}");

        var wireless = new WirelessState(true, savedProfileName, savedProfileName);
        _state.SetState(current with { CapturedAt = DateTimeOffset.Now, Wireless = wireless });
        return wireless;
    }

    public async Task<WirelessState> DisconnectAsync(AdapterKey key, CancellationToken ct = default)
    {
        await Task.Delay(ConnectDelay, ct).ConfigureAwait(false);

        var current = _state.GetState(key)
            ?? throw new InvalidOperationException($"找不到适配器：{key.FriendlyName}");

        var wireless = new WirelessState(false, null, null);
        _state.SetState(current with { CapturedAt = DateTimeOffset.Now, Wireless = wireless });
        return wireless;
    }
}

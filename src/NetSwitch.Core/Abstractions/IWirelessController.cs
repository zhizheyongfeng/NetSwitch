using NetSwitch.Core.Models;

namespace NetSwitch.Core.Abstractions;

public interface IWirelessController
{
    Task<IReadOnlyList<string>> GetSavedProfilesAsync(AdapterKey key, CancellationToken ct = default);

    Task<WirelessState?> GetStateAsync(AdapterKey key, CancellationToken ct = default);

    Task<WirelessState> ConnectAsync(AdapterKey key, string savedProfileName, CancellationToken ct = default);

    Task<WirelessState> DisconnectAsync(AdapterKey key, CancellationToken ct = default);
}

using NetSwitch.Core.Models;

namespace NetSwitch.Core.Abstractions;

public interface IAdapterCatalog
{
    Task<IReadOnlyList<AdapterInfo>> GetAdaptersAsync(CancellationToken ct = default);

    Task<AdapterInfo?> GetAdapterAsync(AdapterKey key, CancellationToken ct = default);

    Task<AdapterState?> GetStateAsync(AdapterKey key, CancellationToken ct = default);
}

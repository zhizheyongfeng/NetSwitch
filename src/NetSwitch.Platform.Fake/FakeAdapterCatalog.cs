using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Platform.Fake;

public sealed class FakeAdapterCatalog : IAdapterCatalog
{
    private readonly FakeNetworkState _state;

    public FakeAdapterCatalog(FakeNetworkState state)
    {
        _state = state;
    }

    public Task<IReadOnlyList<AdapterInfo>> GetAdaptersAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_state.GetAdapters());
    }

    public Task<AdapterInfo?> GetAdapterAsync(AdapterKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_state.GetAdapter(key));
    }

    public Task<AdapterState?> GetStateAsync(AdapterKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_state.GetState(key));
    }
}

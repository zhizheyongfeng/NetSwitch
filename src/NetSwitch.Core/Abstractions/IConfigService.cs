using NetSwitch.Core.Models;

namespace NetSwitch.Core.Abstractions;

public interface IConfigService
{
    AppConfig Config { get; }

    Task<AppConfig> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);

    AdapterConfig GetOrCreateAdapter(AdapterKey key);

    Preset? FindPreset(AdapterKey key, string presetId);

    Task AddOrUpdatePresetAsync(AdapterKey key, Preset preset, CancellationToken ct = default);

    Task DeletePresetAsync(AdapterKey key, string presetId, CancellationToken ct = default);
}

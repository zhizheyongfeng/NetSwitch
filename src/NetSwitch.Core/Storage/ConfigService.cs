using System.Text.Json;
using System.Text.Json.Serialization;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Core.Storage;

public sealed class ConfigService : IConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IPathService _pathService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppConfig _config = new();

    public ConfigService(IPathService pathService)
    {
        _pathService = pathService;
    }

    public AppConfig Config => _config;

    public string ConfigFile => _pathService.Paths.ConfigFile;

    public async Task<AppConfig> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = _pathService.Paths.ConfigFile;
            if (!File.Exists(path))
            {
                _config = new AppConfig();
                _config.Normalize();
                await SaveCoreAsync(ct).ConfigureAwait(false);
                return _config;
            }

            var config = await ReadWithBackupAsync(path, ct).ConfigureAwait(false);
            config.Normalize();
            _config = config;
            return _config;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public AdapterConfig GetOrCreateAdapter(AdapterKey key) => _config.GetOrCreateAdapter(key);

    public Preset? FindPreset(AdapterKey key, string presetId)
    {
        var adapter = _config.Adapters.FirstOrDefault(a => AppConfig.AdapterKeysEqual(a.Key, key));
        return adapter?.Presets.FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.Ordinal));
    }

    public async Task AddOrUpdatePresetAsync(
        AdapterKey key,
        Preset preset,
        CancellationToken ct = default)
    {
        var adapter = GetOrCreateAdapter(key);
        var index = adapter.Presets.FindIndex(p => string.Equals(p.Id, preset.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            adapter.Presets[index] = preset;
        }
        else
        {
            adapter.Presets.Add(preset);
        }

        await SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task DeletePresetAsync(
        AdapterKey key,
        string presetId,
        CancellationToken ct = default)
    {
        var adapter = GetOrCreateAdapter(key);
        adapter.Presets.RemoveAll(p => string.Equals(p.Id, presetId, StringComparison.Ordinal));
        if (string.Equals(adapter.LastAppliedPresetId, presetId, StringComparison.Ordinal))
        {
            adapter.LastAppliedPresetId = null;
            adapter.LastAppliedAt = null;
        }

        await SaveAsync(ct).ConfigureAwait(false);
    }

    private async Task<AppConfig> ReadWithBackupAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                ?? throw new InvalidDataException("配置文件为空。");
        }
        catch (Exception primary) when (primary is JsonException or InvalidDataException)
        {
            var backup = path + ".bak";
            if (!File.Exists(backup))
            {
                throw new InvalidDataException(
                    $"配置文件损坏，且没有可用备份：{primary.Message}",
                    primary);
            }

            try
            {
                var json = await File.ReadAllTextAsync(backup, ct).ConfigureAwait(false);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                    ?? throw new InvalidDataException("备份配置文件为空。");
                return config;
            }
            catch (Exception backupError)
            {
                throw new InvalidDataException(
                    $"配置文件和备份都无法读取：{primary.Message} / {backupError.Message}",
                    backupError);
            }
        }
    }

    private async Task SaveCoreAsync(CancellationToken ct)
    {
        var path = _pathService.Paths.ConfigFile;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            try
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            catch
            {
                // A failed backup must not prevent the primary save.
            }
        }

        var json = JsonSerializer.Serialize(_config, JsonOptions);
        await AtomicFile.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }
}

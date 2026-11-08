using System.Text.Json;
using System.Text.Json.Serialization;
using NetSwitch.Core.Models;

namespace NetSwitch.Core.Storage;

/// <summary>
/// JSON export/import of presets. Import merges into the current configuration instead of
/// replacing it: presets that already exist by name on the same adapter are imported under a
/// new name and id, so an import can never silently overwrite a working preset.
/// </summary>
public static class ConfigTransfer
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task ExportAsync(
        AppConfig config,
        string path,
        CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    public static async Task<AppConfig> ReadAsync(string path, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
            ?? throw new InvalidDataException("导入文件为空或不是有效的 NetSwitch 配置。");
        config.Normalize();
        return config;
    }

    /// <summary>Merges imported presets into <paramref name="target"/>; returns how many were added.</summary>
    public static int Merge(AppConfig target, AppConfig imported)
    {
        var added = 0;
        foreach (var source in imported.Adapters)
        {
            var adapter = target.GetOrCreateAdapter(source.Key);
            foreach (var preset in source.Presets)
            {
                var name = UniqueName(adapter, preset.Name);
                var copy = preset with
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    UpdatedAt = DateTimeOffset.Now
                };
                adapter.Presets.Add(copy);
                added++;
            }
        }

        return added;
    }

    private static string UniqueName(AdapterConfig adapter, string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "导入的预设" : name.Trim();
        if (!adapter.Presets.Any(p =>
                string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        var candidate = $"{baseName}（导入）";
        var index = 2;
        while (adapter.Presets.Any(p =>
                   string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName}（导入 {index}）";
            index++;
        }

        return candidate;
    }
}

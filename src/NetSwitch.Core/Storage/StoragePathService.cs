using System.Text.Json;
using System.Text.Json.Serialization;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Core.Storage;

/// <summary>
/// Resolves all file locations from a single bootstrap file. The bootstrap file is the
/// only fixed location; everything else is configurable.
/// </summary>
public sealed class StoragePathService : IPathService
{
    private const long MinimumFreeBytes = 100L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _bootstrapFile;
    private readonly string _defaultDataRoot;
    private StorageOptions _options;
    private StoragePaths _paths;

    public StoragePathService(string bootstrapFile, string defaultDataRoot)
    {
        _bootstrapFile = Path.GetFullPath(bootstrapFile);
        _defaultDataRoot = Path.GetFullPath(defaultDataRoot);
        _options = StorageOptions.CreateDefault(_defaultDataRoot);
        _paths = ResolvePaths(_options);
    }

    public StoragePaths Paths => _paths;

    public StorageOptions Options => _options;

    public string BootstrapFile => _bootstrapFile;

    public string DefaultDataRoot => _defaultDataRoot;

    public static StoragePathService CreateDefault()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var portableMarker = Path.Combine(baseDirectory, "NetSwitch.portable");

        if (File.Exists(portableMarker))
        {
            return new StoragePathService(
                Path.Combine(baseDirectory, "NetSwitch.locations.json"),
                Path.Combine(baseDirectory, "data"));
        }

        if (OperatingSystem.IsWindows())
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var root = Path.Combine(common, "NetSwitch");
            return new StoragePathService(
                Path.Combine(root, "locations.json"),
                Path.Combine(root, "data"));
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var root = Path.Combine(home, "Library", "Application Support", "NetSwitch");
            return new StoragePathService(
                Path.Combine(root, "locations.json"),
                Path.Combine(root, "data"));
        }

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
        {
            dataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        return new StoragePathService(
            Path.Combine(configHome, "NetSwitch", "locations.json"),
            Path.Combine(dataHome, "NetSwitch", "data"));
    }

    public async Task<StorageInitialization> InitializeAsync(CancellationToken ct = default)
    {
        StorageOptions options;
        try
        {
            options = await LoadOptionsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new StorageInitialization(false, _options, _paths, ex.Message);
        }

        var paths = ResolvePaths(options);
        try
        {
            EnsurePathsAvailable(paths);
            _options = options;
            _paths = paths;
            return new StorageInitialization(true, options, paths, null);
        }
        catch (Exception ex)
        {
            return new StorageInitialization(false, options, paths, ex.Message);
        }
    }

    public async Task<StorageOptions> LoadOptionsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_bootstrapFile))
        {
            var defaults = StorageOptions.CreateDefault(_defaultDataRoot);
            await SaveBootstrapAsync(defaults, ct).ConfigureAwait(false);
            _options = defaults;
            _paths = ResolvePaths(defaults);
            return defaults;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(_bootstrapFile, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new StoragePathUnavailableException(
                $"无法读取引导文件 “{_bootstrapFile}”：{ex.Message}",
                _options);
        }

        StorageOptions? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StorageOptions>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new StoragePathUnavailableException(
                $"引导文件 “{_bootstrapFile}” 格式无效：{ex.Message}",
                _options);
        }

        if (parsed is null)
        {
            throw new StoragePathUnavailableException(
                $"引导文件 “{_bootstrapFile}” 为空。",
                _options);
        }

        var options = Normalize(parsed);
        _options = options;
        _paths = ResolvePaths(options);
        return options;
    }

    public async Task<StorageChangeResult> ChangePathsAsync(
        StorageOptions options,
        bool migrateExisting,
        CancellationToken ct = default)
    {
        var normalized = Normalize(options);
        var newPaths = ResolvePaths(normalized);

        try
        {
            EnsurePathsAvailable(newPaths);
        }
        catch (Exception ex)
        {
            return new StorageChangeResult(false, ex.Message, _paths);
        }

        try
        {
            if (migrateExisting)
            {
                await MigrateAsync(_paths, newPaths, ct).ConfigureAwait(false);
            }

            await SaveBootstrapAsync(normalized, ct).ConfigureAwait(false);
            _options = normalized;
            _paths = newPaths;

            var message = "存储路径已更新。";
            if (newPaths.DataRoot.StartsWith(@"\\", StringComparison.Ordinal))
            {
                message += " 当前路径是 UNC 网络路径；提权后的进程可能看不到用户映射的网络驱动器盘符。";
            }

            return new StorageChangeResult(true, message, newPaths);
        }
        catch (Exception ex)
        {
            return new StorageChangeResult(false, ex.Message, _paths);
        }
    }

    /// <summary>Remembers the last export directory without moving any files.</summary>
    public async Task<StorageChangeResult> RememberExportDirectoryAsync(
        string directory,
        CancellationToken ct = default)
    {
        var options = _options with { LastExportDirectory = directory };
        return await ChangePathsAsync(options, migrateExisting: false, ct).ConfigureAwait(false);
    }

    public static StoragePaths ResolvePaths(StorageOptions options)
    {
        var dataRoot = Path.GetFullPath(options.DataRoot);
        var overrides = options.Overrides ?? new StoragePathOverrides();

        var configFile = Path.Combine(dataRoot, "config.json");
        var snapshots = Path.Combine(dataRoot, "snapshots");
        var logs = Path.Combine(dataRoot, "logs");
        var exports = Path.Combine(dataRoot, "exports");
        var backups = Path.Combine(dataRoot, "backups");

        if (options.Mode == StorageMode.Custom)
        {
            configFile = overrides.ConfigFile ?? configFile;
            snapshots = overrides.SnapshotDirectory ?? snapshots;
            logs = overrides.LogDirectory ?? logs;
            exports = overrides.ExportDirectory ?? exports;
            backups = overrides.BackupDirectory ?? backups;
        }

        return new StoragePaths(
            Path.GetFullPath(configFile),
            Path.GetFullPath(snapshots),
            Path.GetFullPath(logs),
            Path.GetFullPath(exports),
            Path.GetFullPath(backups))
        {
            DataRoot = dataRoot
        };
    }

    private StorageOptions Normalize(StorageOptions options)
    {
        var dataRoot = string.IsNullOrWhiteSpace(options.DataRoot)
            ? _defaultDataRoot
            : Environment.ExpandEnvironmentVariables(options.DataRoot.Trim());

        if (!Path.IsPathRooted(dataRoot))
        {
            throw new InvalidOperationException("数据目录必须是绝对路径。");
        }

        var overrides = options.Overrides ?? new StoragePathOverrides();
        var normalizedOverrides = new StoragePathOverrides(
            NormalizeOptionalPath(overrides.ConfigFile, "配置文件"),
            NormalizeOptionalPath(overrides.SnapshotDirectory, "快照目录"),
            NormalizeOptionalPath(overrides.LogDirectory, "日志目录"),
            NormalizeOptionalPath(overrides.ExportDirectory, "导出目录"),
            NormalizeOptionalPath(overrides.BackupDirectory, "备份目录"));

        var lastExport = NormalizeOptionalPath(options.LastExportDirectory, "上次导出目录")
            ?? Path.Combine(dataRoot, "exports");

        return new StorageOptions(
            options.Mode,
            Path.GetFullPath(dataRoot),
            normalizedOverrides,
            Path.GetFullPath(lastExport));
    }

    private static string? NormalizeOptionalPath(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (!Path.IsPathRooted(expanded))
        {
            throw new InvalidOperationException($"{label}必须是绝对路径：{value}");
        }

        return Path.GetFullPath(expanded);
    }

    private static void EnsurePathsAvailable(StoragePaths paths)
    {
        EnsureDirectoryAvailable(paths.SnapshotDirectory, "快照目录");
        EnsureDirectoryAvailable(paths.LogDirectory, "日志目录");
        EnsureDirectoryAvailable(paths.ExportDirectory, "导出目录");
        EnsureDirectoryAvailable(paths.BackupDirectory, "备份目录");

        var configDirectory = Path.GetDirectoryName(paths.ConfigFile)
            ?? throw new InvalidOperationException("配置文件路径没有父目录。");
        EnsureDirectoryAvailable(configDirectory, "配置文件目录");

        if (Directory.Exists(paths.ConfigFile))
        {
            throw new InvalidOperationException("配置文件路径指向了一个目录。");
        }

        var dataRoot = paths.SnapshotDirectory;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(dataRoot) ?? dataRoot);
            if (drive.IsReady && drive.AvailableFreeSpace < MinimumFreeBytes)
            {
                throw new InvalidOperationException(
                    $"目标磁盘剩余空间不足 100 MB（当前约 {drive.AvailableFreeSpace / 1024 / 1024} MB）。");
            }
        }
        catch (ArgumentException)
        {
            // UNC or unusual path; writable probe below is the authoritative check.
        }
        catch (IOException)
        {
            // The writable probe below is the authoritative check.
        }
    }

    private static void EnsureDirectoryAvailable(string directory, string label)
    {
        var fullPath = Path.GetFullPath(directory);
        if (File.Exists(fullPath))
        {
            throw new InvalidOperationException($"{label}指向了一个文件：{fullPath}");
        }

        try
        {
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{label}不可用：{fullPath}（{ex.Message}）");
        }

        var probe = Path.Combine(fullPath, $".netswitch-write-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{label}不可写：{fullPath}（{ex.Message}）");
        }
        finally
        {
            if (File.Exists(probe))
            {
                try
                {
                    File.Delete(probe);
                }
                catch
                {
                    // Best effort.
                }
            }
        }
    }

    private static async Task MigrateAsync(
        StoragePaths current,
        StoragePaths next,
        CancellationToken ct)
    {
        if (!PathEquals(current.ConfigFile, next.ConfigFile) && File.Exists(current.ConfigFile))
        {
            await AtomicFile.CopyAsync(current.ConfigFile, next.ConfigFile, ct).ConfigureAwait(false);
        }

        await CopyIfNeeded(current.SnapshotDirectory, next.SnapshotDirectory, ct).ConfigureAwait(false);
        await CopyIfNeeded(current.LogDirectory, next.LogDirectory, ct).ConfigureAwait(false);
        await CopyIfNeeded(current.ExportDirectory, next.ExportDirectory, ct).ConfigureAwait(false);
        await CopyIfNeeded(current.BackupDirectory, next.BackupDirectory, ct).ConfigureAwait(false);
    }

    private static async Task CopyIfNeeded(string current, string next, CancellationToken ct)
    {
        if (PathEquals(current, next) || !Directory.Exists(current))
        {
            return;
        }

        await AtomicFile.CopyDirectoryAsync(current, next, ct).ConfigureAwait(false);
    }

    private static bool PathEquals(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }

    private async Task SaveBootstrapAsync(StorageOptions options, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(options, JsonOptions);
        await AtomicFile.WriteAllTextAsync(_bootstrapFile, json, ct).ConfigureAwait(false);
    }
}

using NetSwitch.Core.Models;
using NetSwitch.Core.Storage;
using Xunit;

namespace NetSwitch.Core.Tests;

public sealed class StoragePathServiceTests : IDisposable
{
    private readonly string _root = CreateTempRoot();

    [Fact]
    public async Task InitializeCreatesDirectoriesAndBootstrapFile()
    {
        var paths = await InitializeAsync();

        Assert.True(Directory.Exists(paths.Paths.SnapshotDirectory));
        Assert.True(Directory.Exists(paths.Paths.LogDirectory));
        Assert.True(Directory.Exists(paths.Paths.ExportDirectory));
        Assert.True(Directory.Exists(paths.Paths.BackupDirectory));
        Assert.True(File.Exists(paths.BootstrapFile));
        Assert.Equal(
            Path.Combine(_root, "data", "config.json"),
            paths.Paths.ConfigFile);
    }

    [Fact]
    public void ResolvePathsUsesOverridesOnlyInCustomMode()
    {
        var custom = new StorageOptions(
            StorageMode.Custom,
            Path.Combine(_root, "data"),
            new StoragePathOverrides(ConfigFile: Path.Combine(_root, "cfg.json")));

        Assert.Equal(Path.Combine(_root, "cfg.json"), StoragePathService.ResolvePaths(custom).ConfigFile);

        var unified = custom with { Mode = StorageMode.Unified };
        Assert.Equal(
            Path.Combine(_root, "data", "config.json"),
            StoragePathService.ResolvePaths(unified).ConfigFile);
    }

    [Fact]
    public async Task ChangePathsMigratesExistingConfig()
    {
        var service = await InitializeAsync();
        var oldConfig = service.Paths.ConfigFile;
        await File.WriteAllTextAsync(oldConfig, "{\"version\":1}");

        var next = new StorageOptions(
            StorageMode.Unified,
            Path.Combine(_root, "moved"),
            new StoragePathOverrides());

        var result = await service.ChangePathsAsync(next, migrateExisting: true);

        Assert.True(result.Success, result.Message);
        var migrated = Path.Combine(_root, "moved", "config.json");
        Assert.True(File.Exists(migrated));
        Assert.Equal("{\"version\":1}", await File.ReadAllTextAsync(migrated));

        var reloaded = new StoragePathService(service.BootstrapFile, service.DefaultDataRoot);
        var options = await reloaded.LoadOptionsAsync();
        Assert.Equal(Path.Combine(_root, "moved"), options.DataRoot);
    }

    [Fact]
    public async Task RelativeCustomPathIsRejected()
    {
        var service = await InitializeAsync();
        var invalid = new StorageOptions(
            StorageMode.Custom,
            Path.Combine(_root, "data"),
            new StoragePathOverrides(LogDirectory: "relative/logs"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ChangePathsAsync(invalid, migrateExisting: false));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private async Task<StoragePathService> InitializeAsync()
    {
        var service = new StoragePathService(
            Path.Combine(_root, "locations.json"),
            Path.Combine(_root, "data"));
        var result = await service.InitializeAsync();
        Assert.True(result.Success, result.Error);
        return service;
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netswitch-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class ConfigServiceTests : IDisposable
{
    private static readonly AdapterKey Key = new("fake", "fake-ethernet", "AA:BB:CC:00:00:01", "以太网");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"netswitch-config-tests-{Guid.NewGuid():N}");

    public ConfigServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task PresetsRoundTripThroughJson()
    {
        var pathService = await CreatePathServiceAsync();
        var config = new ConfigService(pathService);
        await config.LoadAsync();

        var preset = Preset();
        await config.AddOrUpdatePresetAsync(Key, preset);

        var reloaded = new ConfigService(pathService);
        await reloaded.LoadAsync();
        var found = reloaded.FindPreset(Key, preset.Id);

        Assert.NotNull(found);
        Assert.Equal(preset.Name, found!.Name);
        Assert.Equal(Ipv4Mode.Static, found.Desired.Ipv4.Mode);
        Assert.Equal(DnsMode.Static, found.Desired.Ipv4.DnsMode);
        Assert.Single(found.Desired.Ipv4.Addresses);
    }

    [Fact]
    public async Task CorruptConfigFallsBackToBackup()
    {
        var pathService = await CreatePathServiceAsync();
        var config = new ConfigService(pathService);
        await config.LoadAsync();

        var first = Preset();
        await config.AddOrUpdatePresetAsync(Key, first);
        await config.AddOrUpdatePresetAsync(Key, Preset("second"));

        await File.WriteAllTextAsync(pathService.Paths.ConfigFile, "{ not valid json");

        var reloaded = new ConfigService(pathService);
        var loaded = await reloaded.LoadAsync();

        Assert.NotNull(reloaded.FindPreset(Key, first.Id));
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task DeletingLastAppliedPresetClearsReference()
    {
        var pathService = await CreatePathServiceAsync();
        var config = new ConfigService(pathService);
        await config.LoadAsync();

        var preset = Preset();
        await config.AddOrUpdatePresetAsync(Key, preset);
        var adapter = config.GetOrCreateAdapter(Key);
        adapter.LastAppliedPresetId = preset.Id;
        adapter.LastAppliedAt = DateTimeOffset.Now;
        await config.SaveAsync();

        await config.DeletePresetAsync(Key, preset.Id);

        Assert.Null(config.GetOrCreateAdapter(Key).LastAppliedPresetId);
        Assert.Null(config.GetOrCreateAdapter(Key).LastAppliedAt);
    }

    [Fact]
    public async Task InvalidJsonWithoutBackupThrows()
    {
        var pathService = await CreatePathServiceAsync();
        await File.WriteAllTextAsync(pathService.Paths.ConfigFile, "{ not valid json");

        var config = new ConfigService(pathService);

        await Assert.ThrowsAsync<InvalidDataException>(() => config.LoadAsync());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private async Task<StoragePathService> CreatePathServiceAsync()
    {
        var service = new StoragePathService(
            Path.Combine(_root, "locations.json"),
            Path.Combine(_root, "data"));
        var result = await service.InitializeAsync();
        Assert.True(result.Success, result.Error);
        return service;
    }

    private static Preset Preset(string name = "软路由-静态") =>
        new(
            Guid.NewGuid().ToString("N"),
            name,
            "测试预设",
            new DesiredState(
                new Ipv4Desired(
                    Ipv4Mode.Static,
                    new[] { new Ipv4Address("192.168.2.100", 24) },
                    new[] { new Gateway("192.168.2.1", 10) },
                    DnsMode.Static,
                    new[] { "192.168.2.1", "223.5.5.5" },
                    MetricMode.Automatic,
                    null),
                Ipv6Desired.Keep(),
                WirelessDesired.Keep()),
            DateTimeOffset.Now,
            DateTimeOffset.Now);
}

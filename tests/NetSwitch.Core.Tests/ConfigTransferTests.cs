using NetSwitch.Core.Models;
using NetSwitch.Core.Storage;
using Xunit;

namespace NetSwitch.Core.Tests;

public sealed class ConfigTransferTests
{
    private static readonly AdapterKey Key = new(
        "windows",
        "11111111-2222-3333-4444-555555555555",
        "00:11:22:33:44:55",
        "测试网卡");

    [Fact]
    public async Task ExportAndReadRoundTripsPresets()
    {
        var config = new AppConfig();
        var adapter = config.GetOrCreateAdapter(Key);
        adapter.Presets.Add(Preset("Home"));

        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "export.json");
            await ConfigTransfer.ExportAsync(config, path);
            var read = await ConfigTransfer.ReadAsync(path);

            var readAdapter = Assert.Single(read.Adapters);
            var preset = Assert.Single(readAdapter.Presets);
            Assert.Equal("Home", preset.Name);
            Assert.Equal(Ipv4Mode.Static, preset.Desired.Ipv4.Mode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MergeAddsPresetsAndRenamesDuplicates()
    {
        var target = new AppConfig();
        var targetAdapter = target.GetOrCreateAdapter(Key);
        targetAdapter.Presets.Add(Preset("Home"));

        var imported = new AppConfig();
        var importedAdapter = imported.GetOrCreateAdapter(Key);
        importedAdapter.Presets.Add(Preset("Home"));
        importedAdapter.Presets.Add(Preset("Office"));

        var added = ConfigTransfer.Merge(target, imported);

        Assert.Equal(2, added);
        Assert.Equal(3, targetAdapter.Presets.Count);
        Assert.Contains(targetAdapter.Presets, p => p.Name == "Office");
        var importedHome = targetAdapter.Presets.Single(p => p.Name == "Home（导入）");
        Assert.Equal("192.168.1.10", importedHome.Desired.Ipv4.Addresses[0].Address);
        Assert.NotEqual(
            targetAdapter.Presets.Single(p => p.Name == "Home").Id,
            importedHome.Id);
    }

    [Fact]
    public void MergeCreatesAdapterForUnknownKey()
    {
        var target = new AppConfig();
        var imported = new AppConfig();
        imported.GetOrCreateAdapter(Key).Presets.Add(Preset("Office"));

        var added = ConfigTransfer.Merge(target, imported);

        Assert.Equal(1, added);
        Assert.Single(target.Adapters);
        Assert.Equal(Key.StableId, target.Adapters[0].Key.StableId);
    }

    [Fact]
    public async Task ReadRejectsInvalidJson()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "bad.json");
            await File.WriteAllTextAsync(path, "{ not json");
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(
                () => ConfigTransfer.ReadAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Preset Preset(string name) => new(
        Guid.NewGuid().ToString("N"),
        name,
        null,
        new DesiredState(
            new Ipv4Desired(
                Ipv4Mode.Static,
                new[] { new Ipv4Address("192.168.1.10", 24) },
                new[] { new Gateway("192.168.1.1") },
                DnsMode.Auto,
                Array.Empty<string>(),
                MetricMode.Keep,
                null),
            Ipv6Desired.Keep(),
            WirelessDesired.Keep()),
        DateTimeOffset.Now,
        DateTimeOffset.Now);

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netswitch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

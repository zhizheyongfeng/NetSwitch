using NetSwitch.Core.Models;
using NetSwitch.Core.Storage;
using Xunit;

namespace NetSwitch.Core.Tests;

public sealed class SnapshotStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"netswitch-snapshots-{Guid.NewGuid():N}");

    private readonly SnapshotStore _store;

    public SnapshotStoreTests()
    {
        Directory.CreateDirectory(_root);
        var paths = new StoragePathService(
            Path.Combine(_root, "locations.json"),
            Path.Combine(_root, "data"));
        _store = new SnapshotStore(paths);
    }

    [Fact]
    public async Task PendingRoundTripsAndCompletes()
    {
        var transaction = NewTransaction(TransactionKind.Apply);

        var path = await _store.WritePendingAsync(transaction);
        Assert.Equal(_store.PendingFile, path);

        var read = await _store.ReadPendingAsync();
        Assert.NotNull(read);
        Assert.Equal(transaction.TransactionId, read!.TransactionId);
        Assert.Equal(AdapterStateWithDhcp().Ipv4.Addresses, read.Snapshot.Ipv4.Addresses);
        Assert.Equal(DnsMode.Static, read.Snapshot.Ipv4.DnsMode);

        var record = await _store.CompleteAsync(
            transaction,
            SnapshotRecord.OutcomeSucceeded,
            null,
            setLast: true);

        Assert.True(record.IsSuccessful);
        Assert.False(File.Exists(_store.PendingFile));
        Assert.True(File.Exists(_store.LastFile));
        Assert.Single(Directory.GetFiles(_store.HistoryDirectory, "*.json"));

        var last = await _store.ReadLastAsync();
        Assert.NotNull(last);
        Assert.Equal(transaction.TransactionId, last!.TransactionId);
        Assert.Equal(AdapterKey, last.AdapterKey);
    }

    [Fact]
    public async Task TestTransactionsNeverBecomeLast()
    {
        var transaction = NewTransaction(TransactionKind.Test);

        await _store.WritePendingAsync(transaction);
        await _store.CompleteAsync(
            transaction,
            SnapshotRecord.OutcomeSucceeded,
            null,
            setLast: true);

        Assert.False(File.Exists(_store.LastFile));
        Assert.Null(await _store.ReadLastAsync());
    }

    [Fact]
    public async Task RollbackFailureKeepsPendingForRecovery()
    {
        var transaction = NewTransaction(TransactionKind.Apply);
        await _store.WritePendingAsync(transaction);

        await _store.CompleteAsync(
            transaction,
            SnapshotRecord.OutcomeRollbackFailed,
            "模拟回滚失败",
            setLast: false,
            keepPending: true);

        Assert.True(File.Exists(_store.PendingFile));
        Assert.NotNull(await _store.ReadPendingAsync());
        Assert.False(File.Exists(_store.LastFile));
    }

    [Fact]
    public async Task HistoryIsPrunedToTheConfiguredLimit()
    {
        for (var i = 0; i < 13; i++)
        {
            var transaction = NewTransaction(TransactionKind.Test) with
            {
                TransactionId = Guid.NewGuid().ToString("N")
            };
            await _store.CompleteAsync(
                transaction,
                SnapshotRecord.OutcomeSucceeded,
                $"第 {i} 次",
                setLast: false);
        }

        Assert.Equal(10, Directory.GetFiles(_store.HistoryDirectory, "*.json").Length);
    }

    [Fact]
    public async Task InvalidPendingJsonIsReported()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_store.PendingFile)!);
        await File.WriteAllTextAsync(_store.PendingFile, "{ not json");

        await Assert.ThrowsAsync<InvalidDataException>(() => _store.ReadPendingAsync());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Test cleanup is best effort.
        }
    }

    private static readonly AdapterKey AdapterKey = new(
        "windows",
        "{11111111-2222-3333-4444-555555555555}",
        "AA-BB-CC-DD-EE-FF",
        "以太网");

    private static PendingTransaction NewTransaction(TransactionKind kind) => new(
        PendingTransaction.CurrentVersion,
        Guid.NewGuid().ToString("N"),
        kind,
        AdapterKey,
        "以太网",
        "preset-1",
        "软路由",
        DateTimeOffset.Now,
        AdapterStateWithDhcp());

    private static AdapterState AdapterStateWithDhcp() => new(
        AdapterKey,
        DateTimeOffset.Now,
        new Ipv4State(
            DhcpEnabled: true,
            new[] { new Ipv4Address("192.168.1.20", 24) },
            new[] { new Gateway("192.168.1.1") },
            DnsMode.Static,
            new[] { "192.168.1.1", "223.5.5.5" },
            AutomaticMetric: true,
            InterfaceMetric: 25),
        new Ipv6State(
            BindingEnabled: true,
            Array.Empty<Ipv4Address>(),
            Array.Empty<Gateway>(),
            Array.Empty<string>(),
            AutomaticMetric: true,
            InterfaceMetric: 25),
        null);
}

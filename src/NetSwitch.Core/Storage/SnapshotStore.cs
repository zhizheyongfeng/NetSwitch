using System.Text.Json;
using System.Text.Json.Serialization;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Core.Storage;

/// <summary>
/// Persists transaction snapshots under the configurable snapshot directory.
///
/// Layout:
///   snapshots/pending.json        present while a transaction is in flight or failed to roll back
///   snapshots/last.json           snapshot taken before the most recent successful apply
///   snapshots/history/*.json      last N finished transactions, including rollbacks
/// </summary>
public sealed class SnapshotStore
{
    private const int HistoryLimit = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IPathService _paths;

    public SnapshotStore(IPathService paths)
    {
        _paths = paths;
    }

    public string PendingFile => Path.Combine(_paths.Paths.SnapshotDirectory, "pending.json");

    public string LastFile => Path.Combine(_paths.Paths.SnapshotDirectory, "last.json");

    public string HistoryDirectory => Path.Combine(_paths.Paths.SnapshotDirectory, "history");

    public async Task<string> WritePendingAsync(
        PendingTransaction transaction,
        CancellationToken ct = default)
    {
        await WriteJsonAsync(PendingFile, transaction, ct).ConfigureAwait(false);
        return PendingFile;
    }

    public async Task<PendingTransaction?> ReadPendingAsync(CancellationToken ct = default)
    {
        if (!File.Exists(PendingFile))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(PendingFile, ct).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<PendingTransaction>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"未完成事务快照格式无效：{PendingFile}（{ex.Message}）",
                ex);
        }
    }

    public async Task<SnapshotRecord?> ReadLastAsync(CancellationToken ct = default)
    {
        if (!File.Exists(LastFile))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(LastFile, ct).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<SnapshotRecord>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"上次配置快照格式无效：{LastFile}（{ex.Message}）",
                ex);
        }
    }

    /// <summary>
    /// Archives a finished transaction and removes pending.json. Set <paramref name="keepPending"/>
    /// when rollback failed, so the next startup can offer recovery again.
    /// </summary>
    public async Task<SnapshotRecord> CompleteAsync(
        PendingTransaction transaction,
        string outcome,
        string? message,
        bool setLast,
        bool keepPending = false,
        CancellationToken ct = default)
    {
        var record = new SnapshotRecord(
            SnapshotRecord.CurrentVersion,
            transaction.TransactionId,
            transaction.Kind,
            outcome,
            transaction.AdapterKey,
            transaction.AdapterName,
            transaction.PresetId,
            transaction.PresetName,
            transaction.StartedAt,
            DateTimeOffset.Now,
            message,
            transaction.Snapshot);

        await WriteJsonAsync(Path.Combine(HistoryDirectory, HistoryFileName(transaction, record)), record, ct)
            .ConfigureAwait(false);

        if (setLast &&
            transaction.Kind == TransactionKind.Apply &&
            string.Equals(outcome, SnapshotRecord.OutcomeSucceeded, StringComparison.Ordinal))
        {
            await WriteJsonAsync(LastFile, record, ct).ConfigureAwait(false);
        }

        if (!keepPending)
        {
            DeletePending();
        }

        PruneHistory();
        return record;
    }

    public void DeletePending()
    {
        try
        {
            if (File.Exists(PendingFile))
            {
                File.Delete(PendingFile);
            }
        }
        catch (IOException)
        {
            // The next transaction start will surface the pending file.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await AtomicFile.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    private static string HistoryFileName(PendingTransaction transaction, SnapshotRecord record)
    {
        var shortId = transaction.TransactionId.Length > 8
            ? transaction.TransactionId[..8]
            : transaction.TransactionId;
        var stamp = record.CompletedAt.ToString("yyyyMMdd-HHmmss-fff");
        return $"{stamp}-{shortId}.json";
    }

    private void PruneHistory()
    {
        try
        {
            if (!Directory.Exists(HistoryDirectory))
            {
                return;
            }

            var files = new DirectoryInfo(HistoryDirectory)
                .EnumerateFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(HistoryLimit)
                .ToArray();

            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch (IOException)
        {
            // History pruning is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // History pruning is best effort.
        }
    }
}

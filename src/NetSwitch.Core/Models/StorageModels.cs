namespace NetSwitch.Core.Models;

public sealed record StoragePathOverrides(
    string? ConfigFile = null,
    string? SnapshotDirectory = null,
    string? LogDirectory = null,
    string? ExportDirectory = null,
    string? BackupDirectory = null);

public sealed record StorageOptions(
    StorageMode Mode,
    string DataRoot,
    StoragePathOverrides? Overrides = null,
    string? LastExportDirectory = null)
{
    public static StorageOptions CreateDefault(string dataRoot) =>
        new(StorageMode.Unified, dataRoot, new StoragePathOverrides(), null);
}

public sealed record StoragePaths(
    string ConfigFile,
    string SnapshotDirectory,
    string LogDirectory,
    string ExportDirectory,
    string BackupDirectory)
{
    public string DataRoot { get; init; } = string.Empty;

    public string SnapshotHistoryDirectory => Path.Combine(SnapshotDirectory, "history");
}

public sealed record StorageChangeResult(
    bool Success,
    string Message,
    StoragePaths EffectivePaths);

public sealed record StorageInitialization(
    bool Success,
    StorageOptions Options,
    StoragePaths Paths,
    string? Error);

public sealed class StoragePathUnavailableException : Exception
{
    public StoragePathUnavailableException(string message, StorageOptions options)
        : base(message)
    {
        Options = options;
    }

    public StorageOptions Options { get; }
}

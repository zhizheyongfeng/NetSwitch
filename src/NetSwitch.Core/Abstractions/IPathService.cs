using NetSwitch.Core.Models;

namespace NetSwitch.Core.Abstractions;

public interface IPathService
{
    StoragePaths Paths { get; }

    StorageOptions Options { get; }

    /// <summary>
    /// Loads the bootstrap file and prepares directories. If the configured location is
    /// unavailable, the result reports the error instead of silently creating an empty config.
    /// </summary>
    Task<StorageInitialization> InitializeAsync(CancellationToken ct = default);

    Task<StorageOptions> LoadOptionsAsync(CancellationToken ct = default);

    Task<StorageChangeResult> ChangePathsAsync(
        StorageOptions options,
        bool migrateExisting,
        CancellationToken ct = default);
}

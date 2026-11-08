using NetSwitch.Core.Models;

namespace NetSwitch.Core.Abstractions;

public interface INetworkConfigurator
{
    Task<ApplyResult> ApplyAsync(
        AdapterKey key,
        DesiredState desired,
        ApplyOptions options,
        CancellationToken ct = default);

    Task<ApplyResult> RestoreAsync(
        AdapterKey key,
        AdapterState snapshot,
        ApplyOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Reports whether a pending transaction exists (crash recovery) and whether a
    /// previous successful apply can be restored with <see cref="RestoreLastAsync"/>.
    /// </summary>
    Task<RecoveryInfo> GetRecoveryInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// Restores the snapshot of an unfinished transaction and clears it on success.
    /// </summary>
    Task<ApplyResult> RecoverPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Re-applies the snapshot taken before the most recent successful apply.
    /// </summary>
    Task<ApplyResult> RestoreLastAsync(CancellationToken ct = default);
}

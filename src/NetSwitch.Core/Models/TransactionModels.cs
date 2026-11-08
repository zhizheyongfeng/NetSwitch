namespace NetSwitch.Core.Models;

/// <summary>
/// Written before any change to the system. If the process crashes mid-transaction,
/// this file is what startup recovery uses to return the adapter to its previous state.
/// </summary>
public sealed record PendingTransaction(
    int Version,
    string TransactionId,
    TransactionKind Kind,
    AdapterKey AdapterKey,
    string AdapterName,
    string? PresetId,
    string? PresetName,
    DateTimeOffset StartedAt,
    AdapterState Snapshot)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// Immutable record of a finished transaction, kept in snapshots/history and, for the
/// most recent successful apply, snapshots/last.json.
/// </summary>
public sealed record SnapshotRecord(
    int Version,
    string TransactionId,
    TransactionKind Kind,
    string Outcome,
    AdapterKey AdapterKey,
    string AdapterName,
    string? PresetId,
    string? PresetName,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? Message,
    AdapterState Snapshot)
{
    public const int CurrentVersion = 1;

    public const string OutcomeSucceeded = "Succeeded";
    public const string OutcomeRolledBack = "RolledBack";
    public const string OutcomeRollbackFailed = "RollbackFailed";
    public const string OutcomeRecovered = "Recovered";

    public bool IsSuccessful => string.Equals(Outcome, OutcomeSucceeded, StringComparison.Ordinal);
}

public sealed record RecoveryInfo(
    bool HasPending,
    TransactionKind? PendingKind,
    string? PendingAdapterName,
    DateTimeOffset? PendingStartedAt,
    bool HasLast,
    string? LastAdapterName,
    DateTimeOffset? LastAppliedAt)
{
    public static RecoveryInfo None { get; } = new(
        false, null, null, null, false, null, null);
}

public sealed record VerificationIssue(string Field, string Expected, string Actual);

public sealed record VerificationResult(IReadOnlyList<VerificationIssue> Issues)
{
    public bool IsMatch => Issues.Count == 0;
}

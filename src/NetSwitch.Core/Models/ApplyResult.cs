namespace NetSwitch.Core.Models;

public sealed record ApplyOptions(
    AutoRevertMode AutoRevertMode = AutoRevertMode.DangerousOnly,
    int AutoRevertSeconds = 60,
    bool ConfirmOnFailure = true,
    string TransactionId = "",
    TransactionKind Kind = TransactionKind.Apply)
{
    public static ApplyOptions Default { get; } = new();
}

public sealed record ApplyResult(
    bool Success,
    ApplyErrorCode ErrorCode,
    string Message,
    AdapterState? Before,
    AdapterState? After,
    string? SnapshotPath,
    TimeSpan Duration)
{
    public static ApplyResult Ok(
        string message,
        AdapterState? before,
        AdapterState? after,
        string? snapshotPath,
        TimeSpan duration) =>
        new(true, ApplyErrorCode.None, message, before, after, snapshotPath, duration);

    public static ApplyResult Fail(
        ApplyErrorCode code,
        string message,
        AdapterState? before = null,
        AdapterState? after = null,
        string? snapshotPath = null,
        TimeSpan duration = default) =>
        new(false, code, message, before, after, snapshotPath, duration);
}

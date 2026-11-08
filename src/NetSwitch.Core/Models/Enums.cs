namespace NetSwitch.Core.Models;

public enum Ipv4Mode
{
    Dhcp,
    Static
}

public enum Ipv6Mode
{
    Keep,
    Auto,
    Static,
    Disabled
}

public enum DnsMode
{
    Auto,
    Static
}

public enum WirelessAction
{
    Keep,
    Connect,
    Disconnect
}

public enum MetricMode
{
    Keep,
    Automatic,
    Manual
}

public enum AutoRevertMode
{
    Off,
    DangerousOnly,
    Always
}

public enum StorageMode
{
    Unified,
    Custom
}

public enum TransactionKind
{
    Apply,
    Test
}

public enum ApplyErrorCode
{
    None,
    NotElevated,
    AdapterNotFound,
    AdapterDisabled,
    WirelessProfileNotFound,
    WirelessConnectFailed,
    ValidationFailed,
    SnapshotFailed,
    ApplyFailed,
    VerificationFailed,
    RollbackFailed,
    PendingRecoveryRequired,
    NotSupported,
    Timeout,
    Cancelled
}

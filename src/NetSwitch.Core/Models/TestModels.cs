namespace NetSwitch.Core.Models;

public sealed record TestOptions(
    bool PingGateway = true,
    bool TestDnsServersDirectly = true,
    bool TestPublicTcp = true,
    string DnsDomain = "www.msftconnecttest.com",
    int TcpPort = 443,
    int TimeoutSeconds = 5,
    int TrialMaxSeconds = 30)
{
    public static TestOptions Default { get; } = new();
}

public enum TestStatus
{
    Pass,
    Warn,
    Fail,
    Skipped,
    Aborted
}

public enum TestOverallStatus
{
    Pass,
    Warn,
    Fail,
    Aborted,
    RestoreFailed
}

public sealed record TestCheck(
    string Name,
    TestStatus Status,
    string Message,
    TimeSpan? Latency = null,
    string? Target = null)
{
    public static TestCheck Pass(string name, string message, TimeSpan? latency = null, string? target = null) =>
        new(name, TestStatus.Pass, message, latency, target);

    public static TestCheck Warn(string name, string message, TimeSpan? latency = null, string? target = null) =>
        new(name, TestStatus.Warn, message, latency, target);

    public static TestCheck Fail(string name, string message, TimeSpan? latency = null, string? target = null) =>
        new(name, TestStatus.Fail, message, latency, target);

    public static TestCheck Skip(string name, string message) =>
        new(name, TestStatus.Skipped, message);
}

public sealed record StaticCheckReport(
    bool CanApply,
    IReadOnlyList<TestCheck> Checks)
{
    public bool HasWarnings => Checks.Any(c => c.Status == TestStatus.Warn);
    public bool HasBlockingErrors => Checks.Any(c => c.Status == TestStatus.Fail);
}

public sealed record ConnectivityTestReport(
    TestOverallStatus Overall,
    bool TrialApplied,
    bool Restored,
    IReadOnlyList<TestCheck> Checks,
    TimeSpan Duration,
    string? SnapshotPath);

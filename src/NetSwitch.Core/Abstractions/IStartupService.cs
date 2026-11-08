namespace NetSwitch.Core.Abstractions;

public sealed record StartupChangeResult(bool Success, string Message);

/// <summary>
/// Registers the application for launch at sign-in. Windows uses Task Scheduler so the
/// task can run elevated without a UAC prompt every boot.
/// </summary>
public interface IStartupService
{
    bool IsSupported { get; }

    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    Task<StartupChangeResult> SetEnabledAsync(bool enabled, CancellationToken ct = default);
}

using NetSwitch.Core.Models;

namespace NetSwitch.Core.Abstractions;

public interface IStaticChecker
{
    Task<StaticCheckReport> ValidateAsync(
        AdapterKey key,
        DesiredState desired,
        CancellationToken ct = default);
}

public interface IPresetTester
{
    Task<StaticCheckReport> ValidateAsync(
        AdapterKey key,
        DesiredState desired,
        CancellationToken ct = default);

    Task<ConnectivityTestReport> RunTrialAsync(
        AdapterKey key,
        DesiredState desired,
        TestOptions options,
        CancellationToken ct = default);
}

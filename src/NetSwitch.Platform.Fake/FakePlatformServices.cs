using NetSwitch.Core.Abstractions;

namespace NetSwitch.Platform.Fake;

public sealed class FakePrivilegeService : IPrivilegeService
{
    public FakePrivilegeService(bool isElevated = true)
    {
        IsElevated = isElevated;
    }

    public bool IsElevated { get; set; }
}

public sealed class FakeStartupService : IStartupService
{
    public bool IsSupported => false;

    public bool Enabled { get; private set; }

    public Task<bool> IsEnabledAsync(CancellationToken ct = default) => Task.FromResult(Enabled);

    public Task<StartupChangeResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        Enabled = enabled;
        return Task.FromResult(new StartupChangeResult(
            true,
            enabled ? "已启用开机自启（演示）。" : "已关闭开机自启（演示）。"));
    }
}

public sealed class FakePlatformCapabilities : IPlatformCapabilities
{
    public string PlatformName => "模拟后端";

    public bool IsSimulated => true;

    public bool SupportsIpv6BindingToggle { get; set; } = true;

    public bool SupportsMultipleIpv4Addresses { get; set; } = true;

    public bool SupportsWirelessProfiles { get; set; } = true;
}

namespace NetSwitch.Core.Abstractions;

public interface IPrivilegeService
{
    bool IsElevated { get; }
}

public interface IPlatformCapabilities
{
    string PlatformName { get; }

    /// <summary>True for the in-memory demo backend; the UI must not claim real changes.</summary>
    bool IsSimulated { get; }

    bool SupportsIpv6BindingToggle { get; }

    bool SupportsMultipleIpv4Addresses { get; }

    bool SupportsWirelessProfiles { get; }
}

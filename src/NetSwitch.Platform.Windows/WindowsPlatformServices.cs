using System.Security.Principal;
using NetSwitch.Core.Abstractions;

namespace NetSwitch.Platform.Windows;

public sealed class WindowsPrivilegeService : IPrivilegeService
{
    public bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}

public sealed class WindowsPlatformCapabilities : IPlatformCapabilities
{
    public string PlatformName => "Windows";

    public bool IsSimulated => false;

    // M3: INetCfg toggles ms_tcpip6; IP Helper writes IPv6 addresses, routes and DNS.
    public bool SupportsIpv6BindingToggle => true;

    public bool SupportsMultipleIpv4Addresses => true;

    // M4: WLAN API lists the current user's saved profiles and connects/disconnects them.
    public bool SupportsWirelessProfiles => true;
}

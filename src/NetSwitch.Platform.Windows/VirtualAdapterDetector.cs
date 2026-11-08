namespace NetSwitch.Platform.Windows;

internal static class VirtualAdapterDetector
{
    private static readonly string[] Markers =
    {
        "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "vbox",
        "tap-windows", "tap adapter", "tunnel", "vpn", "wireguard", "openvpn",
        "tailscale", "zerotier", "docker", "wsl", "npcap", "loopback",
        "bluetooth", "wan miniport", "kernel debug"
    };

    public static bool IsVirtual(string? description, string? friendlyName, uint ifType)
    {
        if (ifType is 24 or 131 or 243 or 244)
        {
            // Loopback, tunnel, and mobile broadband pseudo-interfaces.
            return true;
        }

        var text = $"{description} {friendlyName}".ToLowerInvariant();
        return Markers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
    }
}

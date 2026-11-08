using NetSwitch.Core.Models;

namespace NetSwitch.Core.Validation;

/// <summary>
/// Classifies an apply as low risk or dangerous. Dangerous changes are the ones that can
/// cut the connection the user is currently working over, so they get a confirmation and
/// (when enabled) the auto-revert countdown:
/// <list type="bullet">
/// <item>changing the primary IPv4 address (adding a secondary address is safe);</item>
/// <item>changing the default gateway;</item>
/// <item>disabling the IPv6 binding;</item>
/// <item>disconnecting Wi-Fi on the only connected adapter.</item>
/// </list>
/// DNS-only changes and adding secondary addresses stay low risk.
/// </summary>
public static class ChangeRisk
{
    public static bool IsDangerous(
        DesiredState desired,
        AdapterState? current,
        bool isOnlyConnectedAdapter) =>
        Evaluate(desired, current, isOnlyConnectedAdapter).IsDangerous;

    public static ChangeRiskAssessment Evaluate(
        DesiredState desired,
        AdapterState? current,
        bool isOnlyConnectedAdapter)
    {
        var reasons = new List<string>();

        if (current is null)
        {
            // Without a reliable snapshot any write can lock the machine out; fall back to
            // the conservative classification instead of claiming the apply is safe.
            reasons.Add("无法读取当前网络状态，无法确认变更影响");
            return new ChangeRiskAssessment(true, reasons);
        }

        if (Ipv4PrimaryChanged(desired.Ipv4, current.Ipv4))
        {
            reasons.Add("修改主 IPv4 地址");
        }

        if (GatewayChanged(desired.Ipv4, current.Ipv4))
        {
            reasons.Add("修改默认网关");
        }

        if (Ipv6BindingDisabled(desired.Ipv6, current.Ipv6))
        {
            reasons.Add("禁用 IPv6 绑定");
        }

        if (desired.Wireless is { Action: WirelessAction.Disconnect } &&
            current.Wireless is { IsConnected: true } &&
            isOnlyConnectedAdapter)
        {
            reasons.Add("在唯一联网适配器上断开 Wi-Fi");
        }

        return new ChangeRiskAssessment(reasons.Count > 0, reasons);
    }

    private static bool Ipv4PrimaryChanged(Ipv4Desired desired, Ipv4State current)
    {
        if (desired.Mode == Ipv4Mode.Dhcp)
        {
            // Switching away from a static configuration changes the address set.
            return !current.DhcpEnabled;
        }

        var wanted = desired.Addresses ?? Array.Empty<Ipv4Address>();
        if (wanted.Count == 0)
        {
            return false;
        }

        var existing = current.Addresses ?? Array.Empty<Ipv4Address>();
        if (SameAddresses(wanted, existing))
        {
            return false;
        }

        // Adding one or more secondary addresses without touching the existing ones is safe.
        if (existing.Count > 0 && existing.All(Contains(wanted)))
        {
            return false;
        }

        return true;
    }

    private static bool GatewayChanged(Ipv4Desired desired, Ipv4State current)
    {
        var wanted = desired.Mode == Ipv4Mode.Dhcp
            ? Array.Empty<Gateway>()
            : desired.Gateways ?? Array.Empty<Gateway>();

        // DHCP manages the gateway; only a mode switch from a static configuration changes it.
        if (desired.Mode == Ipv4Mode.Dhcp)
        {
            return !current.DhcpEnabled && (current.Gateways?.Count ?? 0) > 0;
        }

        var existing = current.Gateways ?? Array.Empty<Gateway>();
        var wantedAddresses = wanted.Select(g => g.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingAddresses = existing.Select(g => g.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return !wantedAddresses.SetEquals(existingAddresses);
    }

    private static bool Ipv6BindingDisabled(Ipv6Desired? desired, Ipv6State current)
    {
        if (desired is not { Mode: Ipv6Mode.Disabled })
        {
            return false;
        }

        // Already disabled: re-applying the same state cannot cut connectivity.
        return current.BindingEnabled || current.Mode != Ipv6Mode.Disabled;
    }

    private static bool SameAddresses(IReadOnlyList<Ipv4Address> left, IReadOnlyList<Ipv4Address> right) =>
        left.Count == right.Count && left.All(Contains(right));

    private static Func<Ipv4Address, bool> Contains(IReadOnlyList<Ipv4Address> list) =>
        candidate => list.Any(item =>
            string.Equals(item.Address, candidate.Address, StringComparison.OrdinalIgnoreCase) &&
            item.PrefixLength == candidate.PrefixLength);
}

public sealed record ChangeRiskAssessment(bool IsDangerous, IReadOnlyList<string> Reasons)
{
    public string ReasonText => string.Join("、", Reasons);
}

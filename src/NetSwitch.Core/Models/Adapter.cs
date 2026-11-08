namespace NetSwitch.Core.Models;

/// <summary>
/// Adapter identity. Display name is never used for binding.
/// </summary>
public sealed record AdapterKey(
    string Platform,
    string StableId,
    string MacAddress,
    string FriendlyName)
{
    public static AdapterKey Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(StableId);
}

public enum AdapterKind
{
    Ethernet,
    WiFi,
    Virtual,
    Other
}

public sealed record AdapterInfo(
    AdapterKey Key,
    string Description,
    AdapterKind Kind,
    bool IsUp,
    bool IsPhysical,
    uint InterfaceIndex,
    ulong Luid);

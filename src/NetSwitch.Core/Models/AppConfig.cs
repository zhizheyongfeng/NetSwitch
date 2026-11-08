namespace NetSwitch.Core.Models;

public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    public AppSettings Settings { get; set; } = new();

    public List<AdapterConfig> Adapters { get; set; } = new();

    public void Normalize()
    {
        Settings ??= new AppSettings();
        Settings.Test ??= TestOptions.Default;
        Adapters ??= new List<AdapterConfig>();

        foreach (var adapter in Adapters)
        {
            if (adapter is null)
            {
                continue;
            }

            adapter.Presets ??= new List<Preset>();
            for (var i = 0; i < adapter.Presets.Count; i++)
            {
                var preset = adapter.Presets[i];
                if (preset is null)
                {
                    continue;
                }

                adapter.Presets[i] = preset with { Desired = NormalizeDesired(preset.Desired) };
            }
        }

        Adapters.RemoveAll(a => a is null || a.Key is null || a.Key.IsEmpty);
    }

    public AdapterConfig GetOrCreateAdapter(AdapterKey key)
    {
        var adapter = Adapters.FirstOrDefault(a => AdapterKeysEqual(a.Key, key));
        if (adapter is not null)
        {
            return adapter;
        }

        adapter = new AdapterConfig { Key = key };
        Adapters.Add(adapter);
        return adapter;
    }

    public static bool AdapterKeysEqual(AdapterKey? left, AdapterKey? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(left.Platform, right.Platform, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.StableId, right.StableId, StringComparison.OrdinalIgnoreCase);
    }

    private static DesiredState NormalizeDesired(DesiredState? desired)
    {
        desired ??= DesiredState.Dhcp();

        var ipv4 = desired.Ipv4 ?? Ipv4Desired.Dhcp();
        ipv4 = ipv4 with
        {
            Addresses = ipv4.Addresses ?? Array.Empty<Ipv4Address>(),
            Gateways = ipv4.Gateways ?? Array.Empty<Gateway>(),
            DnsServers = ipv4.DnsServers ?? Array.Empty<string>()
        };

        Ipv6Desired? ipv6 = null;
        if (desired.Ipv6 is not null)
        {
            ipv6 = desired.Ipv6 with
            {
                Addresses = desired.Ipv6.Addresses ?? Array.Empty<Ipv4Address>(),
                Gateways = desired.Ipv6.Gateways ?? Array.Empty<Gateway>(),
                DnsServers = desired.Ipv6.DnsServers ?? Array.Empty<string>()
            };
        }

        return new DesiredState(ipv4, ipv6, desired.Wireless);
    }
}

public sealed class AppSettings
{
    public AutoRevertMode AutoRevertMode { get; set; } = AutoRevertMode.DangerousOnly;

    public int AutoRevertSeconds { get; set; } = 60;

    public bool ConfirmBeforeApply { get; set; } = true;

    public TestOptions Test { get; set; } = TestOptions.Default;

    public bool ShowVirtualAdapters { get; set; }

    public bool CloseToTray { get; set; } = true;

    public bool StartMinimized { get; set; }

    public string Theme { get; set; } = "Light";
}

public sealed class AdapterConfig
{
    public AdapterKey Key { get; set; } = AdapterKey.Empty;

    public List<Preset> Presets { get; set; } = new();

    public string? LastAppliedPresetId { get; set; }

    public DateTimeOffset? LastAppliedAt { get; set; }
}

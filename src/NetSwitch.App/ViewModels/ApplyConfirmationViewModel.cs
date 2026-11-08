using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;

namespace NetSwitch.App.ViewModels;

public sealed record ChangeRowViewModel(
    string Name,
    string Before,
    string After,
    bool Changed);

public sealed class ApplyConfirmationViewModel
{
    public ApplyConfirmationViewModel(
        AdapterInfo adapter,
        Preset preset,
        AdapterState? state,
        bool isSimulated,
        bool isOnlyConnectedAdapter = false)
    {
        AdapterLine = $"{adapter.Key.FriendlyName} · {adapter.Description}";
        PresetName = preset.Name;
        IsSimulated = isSimulated;

        var desired = preset.Desired;
        var ipv4 = state?.Ipv4;
        var ipv6 = state?.Ipv6;
        var wireless = state?.Wireless;

        var rows = new List<ChangeRowViewModel>
        {
            Row("IPv4 地址",
                ipv4 is null ? "未知" : FormatIpv4State(ipv4),
                FormatIpv4Desired(desired.Ipv4)),
            Row("默认网关",
                ipv4 is null ? "未知" : DisplayFormat.Gateways(ipv4.Gateways),
                desired.Ipv4.Mode == Ipv4Mode.Dhcp
                    ? "由 DHCP 下发"
                    : DisplayFormat.Gateways(desired.Ipv4.Gateways)),
            Row("DNS 服务器",
                ipv4 is null ? "未知" : DisplayFormat.Dns(ipv4.DnsServers, ipv4.DnsMode),
                desired.Ipv4.DnsMode == DnsMode.Static
                    ? string.Join(", ", desired.Ipv4.DnsServers)
                    : "由 DHCP 下发"),
            Row("接口跃点",
                ipv4 is null ? "未知" : DisplayFormat.Metric(ipv4.AutomaticMetric, ipv4.InterfaceMetric),
                desired.Ipv4.MetricMode switch
                {
                    MetricMode.Automatic => "自动",
                    MetricMode.Manual => desired.Ipv4.InterfaceMetric?.ToString() ?? "固定",
                    _ => "保持当前值"
                })
        };

        if (desired.Ipv6 is not null && desired.Ipv6.Mode != Ipv6Mode.Keep)
        {
            rows.Add(Row(
                "IPv6",
                ipv6 is null ? "未知" : DisplayFormat.Ipv6Summary(ipv6),
                desired.Ipv6.Mode switch
                {
                    Ipv6Mode.Auto => "自动获取",
                    Ipv6Mode.Disabled => "禁用 IPv6 绑定",
                    Ipv6Mode.Static => DisplayFormat.Addresses(desired.Ipv6.Addresses),
                    _ => "保持系统状态"
                }));
        }

        if (desired.Wireless is not null && desired.Wireless.Action != WirelessAction.Keep)
        {
            rows.Add(Row(
                "Wi-Fi",
                wireless is { IsConnected: true } ? wireless.ActiveProfileName ?? "已连接" : "未连接",
                desired.Wireless.Action == WirelessAction.Disconnect
                    ? "断开连接"
                    : $"连接 {desired.Wireless.SavedProfileName}"));
        }

        Rows = rows;
        var risk = ChangeRisk.Evaluate(desired, state, isOnlyConnectedAdapter);
        IsDangerous = HasChanges && risk.IsDangerous;
        DangerReasons = risk.ReasonText;
    }

    public string PresetName { get; }

    public string AdapterLine { get; }

    public IReadOnlyList<ChangeRowViewModel> Rows { get; }

    public bool IsDangerous { get; }

    /// <summary>Why this apply is considered dangerous, e.g. “修改默认网关”.</summary>
    public string DangerReasons { get; }

    /// <summary>True for the in-memory demo backend; shows the M1 notice in the dialog.</summary>
    public bool IsSimulated { get; }

    public bool HasChanges => Rows.Any(r => r.Changed);

    public string WarningText =>
        "应用会覆盖该适配器当前受管理的网络参数。若当前连接正被远程使用，可能会短暂断网。";

    private static ChangeRowViewModel Row(string name, string before, string after) =>
        new(name, before, after, !string.Equals(before, after, StringComparison.Ordinal));

    private static string FormatIpv4State(Ipv4State ipv4)
    {
        var prefix = ipv4.DhcpEnabled ? "DHCP · " : string.Empty;
        return prefix + DisplayFormat.Addresses(ipv4.Addresses);
    }

    private static string FormatIpv4Desired(Ipv4Desired ipv4) =>
        ipv4.Mode == Ipv4Mode.Dhcp
            ? "DHCP 自动获取"
            : DisplayFormat.Addresses(ipv4.Addresses);
}

using NetSwitch.Core.Models;
using NetSwitch.Core.Validation;

namespace NetSwitch.App.ViewModels;

internal static class DisplayFormat
{
    public static string RelativeTime(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "尚未应用";
        }

        var delta = DateTimeOffset.Now - value.Value;
        if (delta < TimeSpan.Zero)
        {
            return "刚刚";
        }

        if (delta < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)delta.TotalMinutes)} 分钟前";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{(int)delta.TotalHours} 小时前";
        }

        if (delta < TimeSpan.FromDays(2))
        {
            return $"昨天 {value.Value:HH:mm}";
        }

        if (delta < TimeSpan.FromDays(7))
        {
            return $"{(int)delta.TotalDays} 天前";
        }

        return value.Value.ToString("M 月 d 日 HH:mm");
    }

    public static string Addresses(IReadOnlyList<Ipv4Address> addresses)
    {
        if (addresses.Count == 0)
        {
            return "未设置";
        }

        var first = $"{addresses[0].Address}/{addresses[0].PrefixLength}";
        return addresses.Count == 1 ? first : $"{first} 等 {addresses.Count} 个";
    }

    public static string Gateways(IReadOnlyList<Gateway> gateways)
    {
        if (gateways.Count == 0)
        {
            return "未设置";
        }

        var first = gateways[0].Metric is null
            ? gateways[0].Address
            : $"{gateways[0].Address} · 跃点 {gateways[0].Metric}";
        return gateways.Count == 1 ? first : $"{first} 等 {gateways.Count} 个";
    }

    public static string Dns(IReadOnlyList<string> servers, DnsMode mode)
    {
        if (servers.Count == 0)
        {
            return mode == DnsMode.Auto ? "自动" : "未设置";
        }

        var text = string.Join(", ", servers.Take(3));
        if (servers.Count > 3)
        {
            text += $" 等 {servers.Count} 个";
        }

        return mode == DnsMode.Auto ? $"自动 · {text}" : text;
    }

    public static string Ipv6Summary(Ipv6State state)
    {
        if (!state.BindingEnabled)
        {
            return "已禁用";
        }

        // Every interface has a link-local address; show the global one unless the
        // interface only has automatic link-local addresses.
        var global = state.Addresses
            .Where(a => Ipv6AddressUtil.IsGlobalUnicast(a.Address))
            .ToArray();
        var visible = global.Length > 0 ? global : state.Addresses.ToArray();
        if (visible.Length == 0)
        {
            return state.Mode == Ipv6Mode.Static ? "无全局地址" : "自动";
        }

        var first = $"{visible[0].Address}/{visible[0].PrefixLength}";
        return visible.Length == 1 ? first : $"{first} 等 {visible.Length} 个";
    }

    public static string Metric(bool automatic, int metric) =>
        automatic ? $"自动（{metric}）" : metric.ToString();

    public static string TrafficLight(bool isUp) => isUp ? "已连接" : "未连接";
}

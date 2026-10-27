using System.Collections.ObjectModel;
using VoSharp.Ike.Transport;
using VoWin.Helpers;
using VoWin.Models;

namespace VoWin.Services
{


/// <summary>卡槽出口代理解析的单一事实来源。
/// 优先级固定为两层：ICCID 卡规则（持久、随卡走）&gt; IMSI MCC/PLMN 国家规则（持久、随归属国）。
/// 未命中任何规则时为直连；不存在任何槽位级或运行时代理入口。</summary>
public static class EgressRouteResolver
{
    /// <summary>解析结果。命中层 + 实际生效的 socks URL（null=直连）+ 面向用户的说明。</summary>
    public sealed record EgressResolution(
        string Tier,
        string TargetDisplay,
        string Reason,
        string? ProxyUrl,
        bool IsUsable);

    /// <summary>把两层规则解析为一个出口决定。所有调用方（VoWiFi 启动、诊断报告、UI 展示）都必须经过这里。</summary>
    public static EgressResolution Resolve(
        string? iccid,
        string? stableMcc,
        string? simMcc,
        bool hasImsiPlmnConflict,
        ObservableCollection<ProxyNodeModel> proxyPresets,
        ObservableCollection<IccidRouteModel> iccidRoutes,
        ObservableCollection<CountryRouteModel> countryRoutes)
    {
        // Tier 1: ICCID 行的存在本身即最高优先级；无节点的行是显式直连，不落穿。
        var cardRule = string.IsNullOrWhiteSpace(iccid)
            ? null
            : iccidRoutes.FirstOrDefault(route => string.Equals(route.Iccid, iccid.Trim(), StringComparison.Ordinal));
        if (cardRule != null)
        {
            var covered = "已命中这张卡的 ICCID 规则，因此不会继续匹配 IMSI MCC/PLMN 国家规则。";
            if (!string.IsNullOrWhiteSpace(cardRule.ProxyNodeId))
            {
                var node = proxyPresets.FirstOrDefault(candidate => candidate.Id == cardRule.ProxyNodeId);
                if (node == null)
                    return new EgressResolution("ICCID 卡规则（最高优先级）", "直连 UDP（引用节点不存在）",
                        $"ICCID 规则引用的代理节点已被删除。{covered}", null, false);
                if (Socks5Client.TryParse(node.ToProxyUrl()) == null)
                    return new EgressResolution("ICCID 卡规则（最高优先级）", "直连 UDP（节点协议不支持）",
                        $"ICCID 规则引用的节点不是 SOCKS5；VoWiFi 不使用 HTTP/TUN 代理。{covered}", null, false);
                return new EgressResolution("ICCID 卡规则（最高优先级）",
                    $"SOCKS5：{node.Name} ({node.Host}:{node.Port})",
                    $"ICCID {iccid} 已设置专属出口。{covered}", node.ToProxyUrl(), true);
            }

            // ProxyUrl 只承载旧版迁移数据。
            if (!string.IsNullOrWhiteSpace(cardRule.ProxyUrl))
            {
                if (Socks5Client.TryParse(cardRule.ProxyUrl) == null)
                    return new EgressResolution("ICCID 卡规则（最高优先级，旧版地址）", "直连 UDP（保存的代理无效，已忽略）",
                        $"{covered} 保存的地址不是受支持的 socks://、socks5:// 或 socks5h:// 代理。", null, false);
                return new EgressResolution("ICCID 卡规则（最高优先级，旧版地址）",
                    $"SOCKS5：{DescribeProxyEndpoint(cardRule.ProxyUrl)}",
                    $"ICCID {iccid} 已设置专属出口（旧版直存地址）。{covered}", cardRule.ProxyUrl.Trim(), true);
            }

            return new EgressResolution("ICCID 卡规则（最高优先级）", "直连 UDP",
                $"ICCID {iccid} 明确设为直连。{covered}", null, true);
        }

        // Tier 2: 按 IMSI 稳定 MCC 匹配国家规则；国家分流只认 IMSI，不认 ICCID。
        var mcc = stableMcc ?? simMcc;
        var country = string.IsNullOrWhiteSpace(mcc) ? null : MccCountryHelper.FindByMcc(mcc);
        if (country == null)
            return new EgressResolution("无 ICCID 卡规则；IMSI MCC 无法识别", "直连 UDP",
                $"未能由当前 IMSI 解析 MCC（{mcc ?? "--"}），因此没有命中国家分流规则。", null, true);

        var conflictNote = hasImsiPlmnConflict
            ? "同一 ICCID 曾报告不同 PLMN；为避免出口抖动，使用首次稳定 MCC。建议为这张卡配置 ICCID 专属规则。"
            : null;

        var countryRule = countryRoutes.FirstOrDefault(rule =>
            string.Equals(rule.CountryCode, country.Code, StringComparison.OrdinalIgnoreCase));
        if (countryRule is { IsDirect: false } && !string.IsNullOrWhiteSpace(countryRule.ProxyNodeId))
        {
            var node = proxyPresets.FirstOrDefault(candidate => candidate.Id == countryRule.ProxyNodeId);
            if (node == null)
                return new EgressResolution(CountryTier(mcc, country), "直连 UDP（引用节点不存在）",
                    $"国家规则引用的代理节点已删除；不会回退到系统/TUN 代理。{conflictNote}", null, false);
            if (Socks5Client.TryParse(node.ToProxyUrl()) == null)
                return new EgressResolution(CountryTier(mcc, country), "直连 UDP（节点协议不支持）",
                    $"国家规则命中的节点不是 SOCKS5；不会回落到系统/TUN 代理。{conflictNote}", null, false);
            return new EgressResolution(CountryTier(mcc, country),
                $"SOCKS5：{node.Name} ({node.Host}:{node.Port})",
                conflictNote ?? "未找到 ICCID 卡规则，因此由 IMSI 的 MCC 命中次级 PLMN 规则。",
                node.ToProxyUrl(), true);
        }

        var ruleNote = countryRule?.IsDirect == true ? "该国家规则明确配置为直连。" : "未配置该国家的代理规则。";
        return new EgressResolution(CountryTier(mcc, country), "直连 UDP",
            conflictNote != null ? $"{conflictNote} {ruleNote}" : $"未找到 ICCID 卡规则；{ruleNote}", null, true);
    }

    private static string CountryTier(string? mcc, CountryMeta country) =>
        $"自动国家规则（IMSI MCC {mcc} → {country.Flag} {country.Name}）";

    private static string DescribeProxyEndpoint(string proxyUrl)
    {
        // 与 VoWifiRouteDecision 的展示保持一致：只暴露 host:port，凭据由调用方脱敏。
        try
        {
            if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                return uri.Port > 0 ? $"{uri.Host}:{uri.Port}" : uri.Host;
        }
        catch { }
        return "(脱敏地址)";
    }
}

}

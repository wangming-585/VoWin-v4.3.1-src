using System.Collections.ObjectModel;
using VoWin.Models;
using VoWin.Services;
using Xunit;

namespace VoWin.Tests;

/// <summary>
/// 锁定出口代理解析的单一事实来源（EgressRouteResolver）：
/// ICCID 规则绝对优先（含显式直连不落穿）、IMSI MCC 国家规则次级、
/// 引用节点缺失/协议不支持时安全回退直连。
/// </summary>
public class EgressRouteResolverTests
{
    private static ProxyNodeModel Node(string id, string name = "node-a") => new()
    {
        Id = id,
        Name = name,
        Protocol = "socks5",
        Host = "10.0.0.1",
        Port = 1080,
    };

    private static EgressRouteResolver.EgressResolution Resolve(
        string? iccid,
        string? mcc,
        ObservableCollection<ProxyNodeModel> nodes,
        ObservableCollection<IccidRouteModel> iccidRoutes,
        ObservableCollection<CountryRouteModel> countryRoutes,
        bool conflict = false) =>
        EgressRouteResolver.Resolve(iccid, mcc, mcc, conflict, nodes, iccidRoutes, countryRoutes);

    [Fact]
    public void IccidRuleWithNode_WinsOverCountryRule()
    {
        var nodes = new ObservableCollection<ProxyNodeModel> { Node("n1"), Node("n2", "node-b") };
        var iccid = new ObservableCollection<IccidRouteModel>
        {
            new() { Iccid = "8944123456789012", ProxyNodeId = "n1" },
        };
        var country = new ObservableCollection<CountryRouteModel>
        {
            new() { CountryCode = "GB", ProxyNodeId = "n2" },
        };

        var result = Resolve("8944123456789012", "234", nodes, iccid, country);

        Assert.True(result.IsUsable);
        Assert.Equal("n1", ExtractNodeId(nodes, result.ProxyUrl));
        Assert.Contains("ICCID", result.Tier);
    }

    [Fact]
    public void IccidExplicitDirect_DoesNotFallThroughToCountry()
    {
        var nodes = new ObservableCollection<ProxyNodeModel> { Node("n1") };
        var iccid = new ObservableCollection<IccidRouteModel>
        {
            new() { Iccid = "8944A", ProxyNodeId = null, ProxyUrl = null },
        };
        var country = new ObservableCollection<CountryRouteModel>
        {
            new() { CountryCode = "GB", ProxyNodeId = "n1" },
        };

        var result = Resolve("8944A", "234", nodes, iccid, country);

        Assert.Null(result.ProxyUrl);
        Assert.True(result.IsUsable);
        Assert.Contains("ICCID", result.Tier);
    }

    [Fact]
    public void MissingIccidRule_FallsToCountryRule()
    {
        var nodes = new ObservableCollection<ProxyNodeModel> { Node("uk-node", "uk") };
        var country = new ObservableCollection<CountryRouteModel>
        {
            new() { CountryCode = "GB", ProxyNodeId = "uk-node" },
        };

        var result = Resolve(null, "234", nodes, new(), country);

        Assert.True(result.IsUsable);
        Assert.NotNull(result.ProxyUrl);
        Assert.Contains("MCC 234", result.Tier);
    }

    [Fact]
    public void CountryRuleReferencingDeletedNode_SafeDirect()
    {
        var nodes = new ObservableCollection<ProxyNodeModel>();
        var country = new ObservableCollection<CountryRouteModel>
        {
            new() { CountryCode = "GB", ProxyNodeId = "gone" },
        };

        var result = Resolve(null, "234", nodes, new(), country);

        Assert.False(result.IsUsable);
        Assert.Null(result.ProxyUrl);
    }

    [Fact]
    public void UnknownMcc_Direct()
    {
        var result = Resolve(null, "999", new(), new(), new());
        Assert.Null(result.ProxyUrl);
        Assert.True(result.IsUsable);
    }

    [Fact]
    public void PlmnConflict_UsesStableMccAndNotesIt()
    {
        var nodes = new ObservableCollection<ProxyNodeModel> { Node("de-node", "de") };
        var country = new ObservableCollection<CountryRouteModel>
        {
            new() { CountryCode = "DE", ProxyNodeId = "de-node" },
        };

        // stableMcc=262 命中 DE；simMcc 给一个冲突值但 stable 优先。
        var result = EgressRouteResolver.Resolve(
            null, "262", "310", true, nodes, new(), country);

        Assert.True(result.IsUsable);
        Assert.Contains("262", result.Tier);
        Assert.Contains("PLMN", result.Reason);
    }

    private static string? ExtractNodeId(ObservableCollection<ProxyNodeModel> nodes, string? url)
    {
        Assert.NotNull(url);
        return nodes.FirstOrDefault(n => url!.EndsWith($":{n.Port}", StringComparison.Ordinal))?.Id;
    }
}

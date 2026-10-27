using VoWin.Helpers;
using VoWin.Models;
using VoSharp.Sim;

namespace VoWin.Tests;

// ==================== CarrierDisplayHelper ====================
public class CarrierDisplayHelperTests
{
    private static SimIdentity Sim(string mcc, string mnc, string op = "中国移动")
        => new("460001234567890", "8986001234567890123", mcc, mnc, op);

    [Fact]
    public void GetOperatorDisplay_PrefersOperatorName()
        => Assert.Equal("Vodafone", CarrierDisplayHelper.GetOperatorDisplay(Sim("234", "15", "Vodafone")));

    [Fact]
    public void GetOperatorDisplay_ResolvesBrandFromTable_WhenNoSpn()
        => Assert.Equal("Vodafone", CarrierDisplayHelper.GetOperatorDisplay(Sim("234", "15", " ")));

    [Fact]
    public void GetOperatorDisplay_NoName_UnknownPlmn_FallsBackToPlmn()
        => Assert.Equal("PLMN: 999-99", CarrierDisplayHelper.GetOperatorDisplay(Sim("999", "99", " ")));

    [Fact]
    public void GetOperatorDisplay_NullSim()
        => Assert.Equal("未知运营商", CarrierDisplayHelper.GetOperatorDisplay(null));

    [Fact]
    public void GetCountryDisplay_China()
    {
        var d = CarrierDisplayHelper.GetCountryDisplay(Sim("460", "00"));
        Assert.Contains("中国", d);
        Assert.Contains("CN", d);
    }

    [Fact]
    public void GetCountryDisplay_UnknownMcc()
        => Assert.Equal("MCC: 999", CarrierDisplayHelper.GetCountryDisplay(Sim("999", "99")));

    [Fact]
    public void GetCountryDisplay_NullSim() => Assert.Equal("--", CarrierDisplayHelper.GetCountryDisplay(null));

    [Fact]
    public void GetCardProfileDisplay_NullSim()
        => Assert.Equal("未读取 SIM", CarrierDisplayHelper.GetCardProfileDisplay(null));

    [Fact]
    public void GetImsiHomeDisplay_WithPlmn()
    {
        var d = CarrierDisplayHelper.GetImsiHomeDisplay(Sim("460", "00"));
        Assert.Contains("中国", d);
        Assert.Contains("PLMN 460-00", d);
    }

    [Fact]
    public void GetImsiHomeDisplay_NullSim() => Assert.Equal("--", CarrierDisplayHelper.GetImsiHomeDisplay(null));

    [Fact]
    public void GetCountryName_KnownAndUnknown()
    {
        Assert.Equal("中国", CarrierDisplayHelper.GetCountryName(Sim("460", "00")));
        Assert.Equal(string.Empty, CarrierDisplayHelper.GetCountryName(null));
    }

    [Theory]
    [InlineData("PLMN 234-15", true)]
    [InlineData("plmn 234-15", true)]
    [InlineData("234-15", true)]
    [InlineData("46000", true)]
    [InlineData("460001", true)]
    [InlineData("Vodafone", false)]
    [InlineData("中国移动", false)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("12345678", false)] // 8位纯数字不算 PLMN
    public void IsGenericPlmnLabel_Cases(string? label, bool expected)
        => Assert.Equal(expected, CarrierDisplayHelper.IsGenericPlmnLabel(label));

    [Fact]
    public void GetCountryOperatorDisplay_GenericLabelReplacedByTable()
    {
        // SPN 是 "PLMN 234-15"（PC/SC 读卡器常见），应替换为内置表品牌
        var d = CarrierDisplayHelper.GetCountryOperatorDisplay(Sim("234", "15", "PLMN 234-15"));
        Assert.StartsWith("英国", d);
        Assert.DoesNotContain("PLMN 234-15", d);
    }

    [Fact]
    public void GetCountryOperatorDisplay_OverrideWins()
    {
        var d = CarrierDisplayHelper.GetCountryOperatorDisplay(Sim("460", "00", "PLMN 460-00"), "我的英国卡");
        Assert.Contains("我的英国卡", d);
        Assert.Contains("中国", d); // 国家仍来自 IMSI MCC
    }

    [Fact]
    public void GetCountryOperatorDisplay_NothingKnown()
        => Assert.Equal("--", CarrierDisplayHelper.GetCountryOperatorDisplay(null));

    [Fact]
    public void GetOperatorBrand_OverrideUsedDirectly()
        => Assert.Equal("EE", CarrierDisplayHelper.GetOperatorBrand("234", "15", "EE"));

    [Fact]
    public void GetOperatorBrand_GenericFallsBackToTable()
    {
        var brand = CarrierDisplayHelper.GetOperatorBrand("460", "00", "PLMN 460-00");
        Assert.NotEmpty(brand);
        Assert.DoesNotContain("PLMN", brand);
    }

    [Fact]
    public void GetOperatorBrand_Unknown_Empty()
        => Assert.Equal(string.Empty, CarrierDisplayHelper.GetOperatorBrand("999", "99", null));
}

// ==================== 显示模型 ====================
public class DisplayModelTests
{
    [Fact]
    public void SimRouteCardOption_DisplayStrings()
    {
        var opt = new SimRouteCardOption("slot1", "USB 模组 1", "8986001234567890123",
            "460001234567890", "我的卡", "13800138000", "中国移动");
        Assert.Equal("我的卡 · 13800138000", opt.CardAndNumberDisplay);
        Assert.Equal("ICCID 8986001234567890123 · USB 模组 1", opt.IdentityDisplay);
        Assert.Equal("中国移动 · IMSI 460001234567890", opt.NetworkDisplay);
    }

    [Fact]
    public void SimRouteCardOption_NoImsi_NetworkFallsBack()
    {
        var opt = new SimRouteCardOption("s", "n", "iccid", null, "c", "p", "移动");
        Assert.Equal("移动", opt.NetworkDisplay);
    }

    [Fact]
    public void VoWifiRouteDecision_RecordEquality()
    {
        var a = new VoWifiRouteDecision("规则", "目标", "详情", null, false);
        var b = new VoWifiRouteDecision("规则", "目标", "详情", null, false);
        Assert.Equal(a, b);
        Assert.True(a == b);
        var c = a with { UsesProxy = true, ProxyUrl = "socks5://x" };
        Assert.NotEqual(a, c);
        Assert.True(c.UsesProxy);
        Assert.Equal("socks5://x", c.ProxyUrl);
    }
}

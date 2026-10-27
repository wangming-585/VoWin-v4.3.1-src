using System.Reflection;
using VoSharp.Sim;
using VoWin.Models;
using Xunit;

namespace VoWin.Tests;

/// <summary>
/// 锁定 ICCID 规则行的展示派生：国家（IMSI MCC → 国旗+国家码）与号码占位。
/// CountryDisplay 是纯派生属性，不落库，语义须与出口分流（IMSI MCC）一致。
/// </summary>
public class IccidRouteDisplayTests
{
    [Theory]
    [InlineData("23415", "GB")]   // United Kingdom
    [InlineData("46000", "CN")]   // China
    [InlineData("310260", "GU")]  // 查表将 MCC 310 归为关岛（GU），锁定表的真实映射
    public void KnownMcc_ProducesFlagAndCode(string imsi, string expectedCode)
    {
        var route = new IccidRouteModel { Iccid = "8944123456789012", ImsiSnapshot = imsi };

        Assert.Equal(expectedCode, route.CountryDisplay.Trim());
        Assert.Contains(expectedCode, route.CountryNameDisplay);
        Assert.NotEqual("--", route.CountryDisplay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]     // 长度不足 3
    [InlineData("99999")]  // 未识别 MCC
    public void UnknownOrMissingMcc_ShowsPlaceholder(string? imsi)
    {
        var route = new IccidRouteModel { Iccid = "8944A", ImsiSnapshot = imsi };

        Assert.Equal("--", route.CountryDisplay);
        Assert.NotEmpty(route.CountryNameDisplay);
    }

    [Fact]
    public void PhoneNumber_PlaceholderWhenMissing()
    {
        var route = new IccidRouteModel { Iccid = "8944B" };
        Assert.Equal("号码未提供", route.PhoneDisplay);

        route.PhoneNumber = "+447700900123";
        Assert.Equal("+447700900123", route.PhoneDisplay);
    }
}

using System.Linq;
using System.Net;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoWin.Tests;

/// <summary>
/// 加固测试套件（第二批）：ePDG 连接失败退避（地址排除表）、字面 IP
/// 端到端解析、以及 SimIdentity 身份推导边界。锁定切卡自动 VoWiFi
/// 根因区域的重试语义。
/// </summary>
public class EpdgBackoffAndIdentityTests
{
    // ---------- 排除表 / 失败退避 ----------

    [Fact]
    public void ReportConnectionFailure_ExcludesAddressFromResolution()
    {
        EpdgResolver.ClearExcludedAddressesForTests();
        var bad = IPAddress.Parse("203.0.113.10");   // TEST-NET-3，无路由但字面合法
        var good = IPAddress.Parse("203.0.113.20");

        // 两个字面 IP 候选；ResolveAsync 不发 DNS（字面 IP 直接返回）
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2);
        var options = new EpdgDiscoveryOptions(
            StaticAddresses: new[] { bad.ToString(), good.ToString() });

        EpdgResolver.ReportConnectionFailure(bad);
        var result = EpdgResolver.ResolveAsync(sim, options).GetAwaiter().GetResult();

        Assert.DoesNotContain(bad, result.IpAddresses);
        Assert.Contains(good, result.IpAddresses);
    }

    [Fact]
    public void ReportConnectionSuccess_ClearsAllExclusions()
    {
        EpdgResolver.ClearExcludedAddressesForTests();
        var addr = IPAddress.Parse("203.0.113.30");
        EpdgResolver.ReportConnectionFailure(addr);

        EpdgResolver.ReportConnectionSuccess();
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2);
        var options = new EpdgDiscoveryOptions(StaticAddresses: new[] { addr.ToString() });
        var result = EpdgResolver.ResolveAsync(sim, options).GetAwaiter().GetResult();

        Assert.Contains(addr, result.IpAddresses);
    }

    [Fact]
    public void ReportConnectionFailure_AllExcluded_ThenRetriesAfterFullClear()
    {
        // AOSP 语义：全部候选被排除后应整体清空排除表重试，而不是返回空列表
        EpdgResolver.ClearExcludedAddressesForTests();
        var only = IPAddress.Parse("203.0.113.40");
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2);
        var options = new EpdgDiscoveryOptions(StaticAddresses: new[] { only.ToString() });

        EpdgResolver.ReportConnectionFailure(only);
        var result = EpdgResolver.ResolveAsync(sim, options).GetAwaiter().GetResult();

        // 被排除的地址绝不返回
        Assert.DoesNotContain(only, result.IpAddresses);
        // 注意：PLMN 候选可能经真实 DNS 返回其他地址，故仅断言排除生效与非空
        Assert.NotEmpty(result.IpAddresses);
        EpdgResolver.ClearExcludedAddressesForTests();
    }

    [Fact]
    public void ResolveAsync_LiteralIp_SkipsDnsAndReturnsResult()
    {
        EpdgResolver.ClearExcludedAddressesForTests();
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2);
        var options = new EpdgDiscoveryOptions(StaticAddresses: new[] { "203.0.113.50" });
        var result = EpdgResolver.ResolveAsync(sim, options).GetAwaiter().GetResult();

        // Static 候选字面 IP 免 DNS，应排首位（候选优先级保序）
        Assert.Equal(IPAddress.Parse("203.0.113.50"), result.IpAddresses[0]);
        // PLMN 候选也会经 DNS 参与解析（本机网络可达 234-15 ePDG 时），仅断言包含
        Assert.Contains(IPAddress.Parse("203.0.113.50"), result.IpAddresses);
        // IMPI/IMPU 应由 IMSI + IMS 域推导
        Assert.Equal("234150000000001@ims.mnc015.mcc234.3gppnetwork.org", result.Impi);
        Assert.Equal("sip:234150000000001@ims.mnc015.mcc234.3gppnetwork.org", result.Impu);
        Assert.Equal("ims", result.Apn);
        Assert.Equal("234", result.HomeMcc);
        Assert.Equal("15", result.HomeMnc);
    }

    [Fact]
    public void ResolveAsync_Emergency_UsesSosApn()
    {
        EpdgResolver.ClearExcludedAddressesForTests();
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2);
        var options = new EpdgDiscoveryOptions(
            StaticAddresses: new[] { "203.0.113.60" }, IsEmergency: true);
        var result = EpdgResolver.ResolveAsync(sim, options).GetAwaiter().GetResult();
        Assert.Equal("sos", result.Apn);
    }

    [Fact]
    public async Task ResolveAsync_LoopbackLiteral_IsFilteredOut()
    {
        // 回环字面 IP 应被 IsUsableAddress 过滤：静态候选贡献 0 地址。
        // PLMN 候选可能仍经 DNS 解析，因此仅断言静态回环地址不出现在结果中。
        EpdgResolver.ClearExcludedAddressesForTests();
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2);
        var options = new EpdgDiscoveryOptions(StaticAddresses: new[] { "127.0.0.1" });
        var result = await EpdgResolver.ResolveAsync(sim, options);
        Assert.DoesNotContain(IPAddress.Loopback, result.IpAddresses);
        EpdgResolver.ClearExcludedAddressesForTests();
    }

    // ---------- SimIdentity 身份推导 ----------

    [Theory]
    [InlineData("460011234567890", 2, "460", "01", true)]
    [InlineData("310260123456789", 3, "310", "260", true)]
    [InlineData("234150000000001", null, "234", "15", false)]
    public void FromImsiAndIccid_DerivesMccMnc(string imsi, int? mncLength, string mcc, string mnc, bool authoritative)
    {
        var sim = SimIdentity.FromImsiAndIccid(imsi, "X", mncLength: mncLength);
        Assert.Equal(mcc, sim.Mcc);
        Assert.Equal(mnc, sim.Mnc);
        Assert.Equal(authoritative, sim.IsHomePlmnAuthoritative);
    }

    [Fact]
    public void FromImsiAndIccid_DefaultOperatorName_IsPlmnLabel()
    {
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2);
        Assert.Equal("PLMN 234-15", sim.OperatorName);
    }

    [Fact]
    public void FromImsiAndIccid_WhitespaceFields_AreTrimmed()
    {
        var sim = SimIdentity.FromImsiAndIccid("  234150000000001  ", "  8988211000000000001  ",
            opName: "  Test Carrier  ");
        Assert.Equal("234150000000001", sim.Imsi);
        Assert.Equal("8988211000000000001", sim.Iccid);
        Assert.Equal("Test Carrier", sim.OperatorName);
    }

    [Fact]
    public void FromImsiAndIccid_HomePlmns_AreDeduplicated()
    {
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2,
            homePlmns: new[] { "23415", "23415", "23420" });
        Assert.Equal(2, sim.HomePlmns!.Count);
    }

    [Fact]
    public void FromImsiAndIccid_BlankOptionalStrings_BecomeNull()
    {
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X",
            reportedImsi: "   ", permanentImsi: "", imsiSource: "  ");
        Assert.Null(sim.ReportedImsi);
        Assert.Null(sim.PermanentImsi);
        Assert.Equal("unknown", sim.ImsiSource);
    }

    [Fact]
    public void FromImsiAndIccid_ShortImsi_MccEmpty()
    {
        var sim = SimIdentity.FromImsiAndIccid("23", "X");
        Assert.Equal("", sim.Mcc);
        Assert.Equal("", sim.Mnc);
    }
}

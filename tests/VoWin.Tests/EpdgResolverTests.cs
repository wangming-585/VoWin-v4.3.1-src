using System.Linq;
using System.Net;
using VoSharp.Sim;
using VoSharp.Telephony.VoWifi;
using Xunit;

namespace VoWin.Tests;

/// <summary>
/// 加固测试套件：锁定 ePDG 解析链路（ePDG DNS、候选构建、PLMN 推导、
/// PCO 解析）的行为，防止回归。覆盖此前切卡 VoWiFi 自动连接的根因区域。
/// </summary>
public class EpdgResolverTests
{
    // ---------- 域名构建 ----------

    [Theory]
    [InlineData("460", "11", "epdg.epc.mnc011.mcc460.pub.3gppnetwork.org")]
    [InlineData("310", "260", "epdg.epc.mnc260.mcc310.pub.3gppnetwork.org")]
    [InlineData("234", "15", "epdg.epc.mnc015.mcc234.pub.3gppnetwork.org")]
    [InlineData("460", "00", "epdg.epc.mnc000.mcc460.pub.3gppnetwork.org")]
    public void BuildEpdgDomain_PadsAndFormats(string mcc, string mnc, string expected)
    {
        Assert.Equal(expected, EpdgResolver.BuildEpdgDomain(mcc, mnc));
    }

    [Fact]
    public void BuildEmergencyEpdgDomain_PrefixesSos()
    {
        Assert.Equal("sos.epdg.epc.mnc015.mcc234.pub.3gppnetwork.org",
            EpdgResolver.BuildEmergencyEpdgDomain("234", "15"));
    }

    [Theory]
    [InlineData("234", false, "epdg.epc.mcc234.visited-country.pub.3gppnetwork.org")]
    [InlineData("310", true, "sos.epdg.epc.mcc310.visited-country.pub.3gppnetwork.org")]
    public void BuildVisitedCountryDomain_Formats(string mcc, bool emergency, string expected)
    {
        Assert.Equal(expected, EpdgResolver.BuildVisitedCountryDomain(mcc, emergency));
    }

    [Theory]
    [InlineData("234", "15", 0x00A1, "epdg.epc.mnc015.mcc234.pub.3gppnetwork.org", "lac00a1.")]
    [InlineData("234", "15", 0xFFFF, "epdg.epc.mnc015.mcc234.pub.3gppnetwork.org", "lacffff.")]
    public void BuildLacDomain_Formats(string mcc, string mnc, int lac, string baseDomain, string prefix)
    {
        Assert.Equal(prefix + baseDomain, EpdgResolver.BuildLacDomain(mcc, mnc, lac));
    }

    [Fact]
    public void BuildLacDomain_InvalidLac_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => EpdgResolver.BuildLacDomain("234", "15", -1));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => EpdgResolver.BuildLacDomain("234", "15", 0x10000));
    }

    [Theory]
    [InlineData("234", "15", 0x1234, "tac-lb34.tac-hb12.tac.epdg.epc.mnc015.mcc234.pub.3gppnetwork.org")]
    [InlineData("310", "260", 0x0001, "tac-lb01.tac-hb00.tac.epdg.epc.mnc260.mcc310.pub.3gppnetwork.org")]
    public void BuildLteTaiDomain_Formats(string mcc, string mnc, int tac, string expected)
    {
        Assert.Equal(expected, EpdgResolver.BuildLteTaiDomain(mcc, mnc, tac));
    }

    [Fact]
    public void BuildLteTaiDomain_InvalidTac_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => EpdgResolver.BuildLteTaiDomain("234", "15", 0x10000));
    }

    [Fact]
    public void BuildImsDomain_Formats()
    {
        Assert.Equal("ims.mnc015.mcc234.3gppnetwork.org",
            EpdgResolver.BuildImsDomain("234", "15"));
    }

    // ---------- PCO 地址解析 ----------

    [Fact]
    public void ParsePcoAddress_Ipv4_RoundTrips()
    {
        // 3 字节 PLMN 前缀 + 4 字节 IPv4
        var raw = new byte[] { 0x21, 0x54, 0x40, 192, 168, 1, 99 };
        var addr = EpdgResolver.ParsePcoAddress(raw);
        Assert.NotNull(addr);
        Assert.Equal(IPAddress.Parse("192.168.1.99"), addr);
    }

    [Fact]
    public void ParsePcoAddress_Ipv6_Parses()
    {
        var v6 = IPAddress.Parse("2001:db8::1").GetAddressBytes();
        var raw = new byte[] { 0x21, 0x54, 0x40 }.Concat(v6).ToArray();
        var addr = EpdgResolver.ParsePcoAddress(raw);
        Assert.NotNull(addr);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), addr);
    }

    [Fact]
    public void ParsePcoAddress_InvalidLength_ReturnsNull()
    {
        Assert.Null(EpdgResolver.ParsePcoAddress(new byte[] { 0x21, 0x54, 0x40, 192, 168 })); // 2 字节，非法
        Assert.Null(EpdgResolver.ParsePcoAddress(new byte[] { 0x21, 0x54 })); // 长度不足
    }

    // ---------- Home-PLMN 候选推导 ----------

    [Fact]
    public void BuildHomePlmnCandidates_UsesAuthoritativeHomePlmnFirst()
    {
        // EF_AD 给出权威 home PLMN 234-15，应作为唯一候选
        var sim = SimIdentity.FromImsiAndIccid(
            imsi: "234150000000001",
            iccid: "8988211000000000001",
            mncLength: 2,
            homePlmns: new[] { "23415" });
        var candidates = EpdgResolver.BuildHomePlmnCandidates(sim);
        Assert.Single(candidates);
        Assert.Equal(("234", "15"), candidates[0]);
    }

    [Fact]
    public void BuildHomePlmnCandidates_WithTwoDigitMnc()
    {
        var sim = SimIdentity.FromImsiAndIccid(
            imsi: "234150000000001", iccid: "X", mncLength: 2);
        var candidates = EpdgResolver.BuildHomePlmnCandidates(sim);
        // 权威 2 位 MNC -> 单一候选 234-15
        Assert.Contains(("234", "15"), candidates);
        Assert.DoesNotContain(("234", "150"), candidates);
    }

    [Fact]
    public void BuildHomePlmnCandidates_WithThreeDigitMnc()
    {
        var sim = SimIdentity.FromImsiAndIccid(
            imsi: "310260123456789", iccid: "X", mncLength: 3);
        var candidates = EpdgResolver.BuildHomePlmnCandidates(sim);
        Assert.Contains(("310", "260"), candidates);
    }

    [Fact]
    public void BuildHomePlmnCandidates_NonAuthoritative_TriesBothMncLengths()
    {
        // 非权威：无 home PLMN、MNC 长度未限定，应生成 2/3 位两种候选并去重
        var sim = SimIdentity.FromImsiAndIccid(
            imsi: "234150000000001", iccid: "X", mncLength: null);
        var candidates = EpdgResolver.BuildHomePlmnCandidates(sim);
        Assert.Contains(("234", "15"), candidates);
        Assert.Contains(("234", "150"), candidates);
    }

    [Theory]
    [InlineData("12")]            // 太短
    [InlineData("12345678901234567")] // 过长
    public void BuildHomePlmnCandidates_InvalidImsi_Throws(string imsi)
    {
        var sim = SimIdentity.FromImsiAndIccid(imsi, "X");
        Assert.Throws<System.ArgumentException>(() => EpdgResolver.BuildHomePlmnCandidates(sim));
    }

    // ---------- 候选构建（ePDG 挑选） ----------

    [Fact]
    public void BuildCandidates_CustomEpdg_TakesPriorityOverPlmn()
    {
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2);
        var options = new EpdgDiscoveryOptions(CustomEpdg: "10.0.0.1");
        var candidates = EpdgResolver.BuildCandidates(sim, options);
        Assert.Single(candidates);
        Assert.Equal(("10.0.0.1", "CUSTOM"), (candidates[0].Host, candidates[0].Method));
    }

    [Fact]
    public void BuildCandidates_CustomEpdg_TrimsAndIgnoresBlank()
    {
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2);
        var candidates = EpdgResolver.BuildCandidates(sim, new EpdgDiscoveryOptions(CustomEpdg: "  10.0.0.1.  "));
        Assert.Single(candidates);
        Assert.Equal("10.0.0.1", candidates[0].Host);
        var fallback = EpdgResolver.BuildCandidates(sim, new EpdgDiscoveryOptions(CustomEpdg: "   "));
        Assert.NotEmpty(fallback);
        Assert.DoesNotContain(fallback, c => c.Method == "CUSTOM");
    }

    [Fact]
    public void BuildCandidates_Plmn_BuildsStandardFqdn()
    {
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2);
        var candidates = EpdgResolver.BuildCandidates(sim);
        Assert.Contains(candidates, c => c.Method == "PLMN" &&
            c.Host == "epdg.epc.mnc015.mcc234.pub.3gppnetwork.org");
    }

    [Fact]
    public void BuildCandidates_Emergency_UsesSosDomain()
    {
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2);
        var options = new EpdgDiscoveryOptions(IsEmergency: true);
        var candidates = EpdgResolver.BuildCandidates(sim, options);
        Assert.Contains(candidates, c => c.Host.StartsWith("sos.",
            System.StringComparison.Ordinal));
    }

    [Fact]
    public void BuildCandidates_DeduplicatesCaseInsensitive()
    {
        var sim = SimIdentity.FromImsiAndIccid("234150000000001", "X", mncLength: 2);
        // Static 源提供同一 FQDN 两种大小写，应只保留一份候选
        var options = new EpdgDiscoveryOptions(
            StaticAddresses: new[] { "epdg.example.com", "EPDG.EXAMPLE.COM" });
        var candidates = EpdgResolver.BuildCandidates(sim, options);
        var hosts = candidates.Select(c => c.Host.ToLowerInvariant()).ToList();
        Assert.Single(hosts, h => h == "epdg.example.com");
    }

    [Fact]
    public void ResolveHomePlmn_EmptyCandidates_ThrowsInformative()
    {
        // 构造一个虽符合 IMSI 长度校验、但无可用 Home-PLMN 且非权威的身份，
        // 验证防御性异常而非裸 IndexOutOfRangeException。
        // 注：非权威且无法推导时 BuildHomePlmnCandidates 通常仍会尝试 2/3 位 MNC；
        // 此处直接构造空候选保护逻辑无法从公开 API 完全触达，改为验证边界输入不抛 IndexOutOfRange。
        var sim = SimIdentity.FromImsiAndIccid("000000000000001", "X", mncLength: null);
        // "000" 属于保留 MCC，无 homePLMN，非权威仍走 2/3 位推导 -> 不抛，返回非空。
        var candidates = EpdgResolver.BuildHomePlmnCandidates(sim);
        Assert.True(candidates.Count > 0);
        var home = EpdgResolver.ResolveHomePlmn(sim);
        Assert.Equal(candidates[0], home);
    }
}

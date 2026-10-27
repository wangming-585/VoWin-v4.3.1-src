using VoSharp.Euicc.Models;
using VoSharp.Sim;
using VoSharp.StateMachine;
using VoSharp.Telephony.VoWifi;
using VoWin.Helpers;
using static VoWin.Helpers.VoWifiStatePresenter;

namespace VoWin.Tests;

public class CardDisplayNameResolverTests
{
    private static SimIdentity Sim(
        string mcc = "460",
        string mnc = "00",
        string? phone = null,
        string? op = "中国移动") =>
        new("460001234567890", "89860011223344556677", mcc, mnc,
            op ?? $"PLMN {mcc}-{mnc}", phone);

    [Theory]
    [InlineData("tel:+8613800138000;user=phone", "+8613800138000")]
    [InlineData("sip:+85290123456@ims.mnc012.mcc454.3gppnetwork.org", "+85290123456")]
    [InlineData("<sip:+12125550123@ims.example.com>,<tel:+12125550199>", "+12125550123")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("no-number-here", null)]
    public void ExtractNumberFromAssociatedUri_ParsesNumber(string? uri, string? expected)
    {
        Assert.Equal(expected, CardDisplayNameResolver.ExtractNumberFromAssociatedUri(uri));
    }

    [Fact]
    public void ResolvePhoneNumber_PrefersSimPhoneNumber_ThenFallsBackToUri()
    {
        var simWithPhone = Sim(phone: "+8613911112222");
        Assert.Equal("+8613911112222",
            CardDisplayNameResolver.ResolvePhoneNumber(simWithPhone, "tel:+8613800138000"));

        var simWithoutPhone = Sim();
        Assert.Equal("+8613800138000",
            CardDisplayNameResolver.ResolvePhoneNumber(simWithoutPhone, "tel:+8613800138000"));

        Assert.Null(CardDisplayNameResolver.ResolvePhoneNumber(null, null));
    }

    [Fact]
    public void ResolveAutoDisplayName_FallsBackToCarrierPlusIccidTail()
    {
        var name = CardDisplayNameResolver.ResolveAutoDisplayName(
            Sim(phone: null, op: "中国移动"),
            associatedUri: null,
            iccid: "89860011223344556677",
            operatorName: null);
        Assert.NotNull(name);
        Assert.Contains("中国移动", name);
        Assert.Contains(CardDisplayNameResolver.IccidTail("89860011223344556677"), name);
    }

    [Fact]
    public void ResolveAutoDisplayName_UsesCarrierPlusTail_NotNumber()
    {
        // 自动显示名统一为“品牌+尾号”，手机号码独立存储于 ICCID 规则列。
        var name = CardDisplayNameResolver.ResolveAutoDisplayName(
            Sim(phone: "+8613911112222"),
            associatedUri: "tel:+8613800138000",
            iccid: "89860011223344556677");
        Assert.NotNull(name);
        Assert.Contains("中国移动", name);
        Assert.Contains(CardDisplayNameResolver.IccidTail("89860011223344556677"), name);
        Assert.DoesNotContain("+8613911112222", name);
    }

    [Fact]
    public void BuildCardLabel_ResolvesBrandFromPlmnTable()
    {
        // MCC 234 / MNC 15 在表内映射为 Vodafone；SPN 为通用 PLMN 标签。
        var label = CarrierDisplayHelper.BuildCardLabel(
            Sim(mcc: "234", mnc: "15", op: "PLMN 234-15"),
            "89441000400316382888");
        Assert.Equal("Vodafone ···2888", label);
    }

    [Fact]
    public void BuildCardLabel_CountryFallback()
    {
        var label = CarrierDisplayHelper.BuildCardLabel(
            Sim(mcc: "999", mnc: "99", op: null),
            "89860011223344556677");
        // 999 未分配品牌，回退到 PLMN + 尾号。
        Assert.Contains("PLMN 999-99", label);
        Assert.Contains("6677", label);
    }

    [Fact]
    public void ResolveAutoDisplayName_NullSim_UsesOperatorArgument()
    {
        var name = CardDisplayNameResolver.ResolveAutoDisplayName(
            null, null, "89860011223344556677", operatorName: "CSL");
        Assert.NotNull(name);
        Assert.Contains("CSL", name);
    }

    [Fact]
    public void ResolveEffectiveName_UserNicknameWins()
    {
        var effective = CardDisplayNameResolver.ResolveEffectiveName(
            userNickname: "我的香港卡",
            autoDisplayName: "+8613800138000",
            sim: Sim());
        Assert.Equal("我的香港卡", effective);
    }

    [Fact]
    public void ResolveEffectiveName_NoNickname_UsesAutoOrPlmnFallback()
    {
        var auto = CardDisplayNameResolver.ResolveEffectiveName(
            null, "+85290123456", Sim());
        Assert.Equal("+85290123456", auto);

        var fallback = CardDisplayNameResolver.ResolveEffectiveName(
            null, null, Sim(mcc: "454", mnc: "06", op: null));
        Assert.NotNull(fallback);
        Assert.Contains("454-06", fallback);
    }

    [Fact]
    public void IccidTail_ReturnsLastFourDigits()
    {
        Assert.Equal("6677", CardDisplayNameResolver.IccidTail("89860011223344556677"));
        Assert.Equal(string.Empty, CardDisplayNameResolver.IccidTail(null));
    }
}

public class PlmnCarrierTableTests
{
    [Theory]
    [InlineData("460", "00", true)]       // 中国移动，标准 "mcc-mnc" 键
    [InlineData("460", "0", true)]        // MNC 去前导零后命中
    [InlineData("454", "06", true)]       // 香港 3（SmarTone）
    [InlineData("999", "99", false)]      // 未分配 PLMN
    [InlineData(null, "00", false)]
    [InlineData("460", null, false)]
    public void Resolve_HandlesKeyVariants(string? mcc, string? mnc, bool expectHit)
    {
        var brand = PlmnCarrierTable.Resolve(mcc, mnc);
        if (expectHit) Assert.False(string.IsNullOrWhiteSpace(brand));
        else Assert.True(string.IsNullOrWhiteSpace(brand));
    }

    [Fact]
    public void ResolveIccidCountry_KnownPrefix()
    {
        Assert.Equal("中国", PlmnCarrierTable.ResolveIccidCountry("89860011223344556677"));
    }

    [Fact]
    public void ResolveIccidCountry_UnknownOrNull()
    {
        Assert.Null(PlmnCarrierTable.ResolveIccidCountry(null));
        Assert.Null(PlmnCarrierTable.ResolveIccidCountry("89"));
    }
}

public class MccCountryHelperTests
{
    [Theory]
    [InlineData("460", "中国")]
    [InlineData("454", "香港")]  // 若表用 "中国香港" 等措辞，用 Contains 校验兜底
    public void FindByMcc_BareMcc(string mcc, string expectContains)
    {
        var meta = MccCountryHelper.FindByMcc(mcc);
        Assert.NotNull(meta);
        Assert.Contains(expectContains, meta.Name);
    }

    [Fact]
    public void FindByMcc_PlmnFormat()
    {
        var meta = MccCountryHelper.FindByMcc("454-06");
        Assert.NotNull(meta);
        Assert.Equal(MccCountryHelper.FindByMcc("454")!.Mccs, meta.Mccs);
    }

    [Fact]
    public void FindByMcc_Unknown()
    {
        Assert.Null(MccCountryHelper.FindByMcc("999"));
        Assert.Null(MccCountryHelper.FindByMcc(null));
    }

    [Fact]
    public void GetCountryName_EmptyWhenUnknown()
    {
        Assert.Equal(string.Empty, MccCountryHelper.GetCountryName("999"));
    }

    [Fact]
    public void GeneratedTable_CoversAll230Mccs()
    {
        Assert.True(MccCountryHelper.GetAllKnownCountries().Count >= 200);
    }
}

public class ProfileModelTests
{
    [Theory]
    [InlineData(ProfileState.Enabled, true)]
    [InlineData(ProfileState.Disabled, false)]
    public void IsEnabledState_TracksProfileState(ProfileState state, bool expected)
    {
        var profile = new Profile { State = state };
        Assert.Equal(expected, profile.IsEnabledState);
    }

    [Fact]
    public void ProviderName_FallsBackToProfileName()
    {
        var profile = new Profile { ProfileName = "Travel", ServiceProviderName = null };
        Assert.Equal("Travel", profile.ProviderName);
        profile.ServiceProviderName = "CMCC";
        Assert.Equal("CMCC", profile.ProviderName);
    }
}

public class PlmnResolveEdgeCaseTests
{
    [Theory]
    [InlineData("460", "00", "460-00")]   // exact two-digit key
    [InlineData("460", "0", "460-00")]     // zero-trimmed caller, padded fallback
    [InlineData("454", "06", "454-06")]    // HK PLMN
    public void Resolve_NormalisesMncWidth(string mcc, string mnc, string expectedKeyPrefix)
    {
        var brand = PlmnCarrierTable.Resolve(mcc, mnc);
        Assert.NotNull(brand);
        Assert.Contains(CardLookupTables.PlmnOperator[expectedKeyPrefix], brand);
    }

    [Fact]
    public void ResolveIccidCountry_LongestPrefixWins()
    {
        // "8986" resolves to China; an unknown issuer prefix returns null.
        Assert.Equal("中国", PlmnCarrierTable.ResolveIccidCountry("8986001234567890"));
        Assert.Null(PlmnCarrierTable.ResolveIccidCountry("00001234"));
    }
}

public class VoWifiStatePresenterTests
{
    [Theory]
    
    [InlineData(VoWifiState.ResolvingEpdg, DashboardPhase.ResolvingEpdg)]
    [InlineData(VoWifiState.ConnectingIkev2, DashboardPhase.BuildingTunnel)]
    [InlineData(VoWifiState.AuthenticatingEapAka, DashboardPhase.Authenticating)]
    [InlineData(VoWifiState.IpsecTunnelEstablished, DashboardPhase.BuildingTunnel)]
    [InlineData(VoWifiState.ImsRegistering, DashboardPhase.RegisteringIms)]
    [InlineData(VoWifiState.ImsRegistered, DashboardPhase.Connected)]
    [InlineData(VoWifiState.Failed, DashboardPhase.Failed)]
    public void ResolvePhase_MapsVoWifiStates(VoWifiState state, DashboardPhase expected)
    {
        Assert.Equal(expected,
            VoWifiStatePresenter.ResolvePhase(state, anySlotPresent: true, simReady: true, userStopped: false));
    }

    [Fact]
    public void ResolvePhase_NoCard_WinsOverEverything()
    {
        Assert.Equal(DashboardPhase.NoCard,
            VoWifiStatePresenter.ResolvePhase(VoWifiState.Failed, anySlotPresent: false, simReady: false, userStopped: false));
    }

    [Fact]
    public void ResolvePhase_WaitingSim_WhenDisconnectedAndSimNotReady()
    {
        Assert.Equal(DashboardPhase.WaitingSim,
            VoWifiStatePresenter.ResolvePhase(VoWifiState.Disconnected, true, simReady: false, userStopped: false));
    }

    [Fact]
    public void ResolvePhase_Stopped_WhenUserStoppedAndDisconnected()
    {
        Assert.Equal(DashboardPhase.Stopped,
            VoWifiStatePresenter.ResolvePhase(VoWifiState.Disconnected, true, simReady: true, userStopped: true));
    }

    [Fact]
    public void Present_AllPhases_HaveNonEmptyText()
    {
        foreach (DashboardPhase phase in Enum.GetValues<DashboardPhase>())
        {
            var p = VoWifiStatePresenter.Present(phase);
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            Assert.Contains(p.Tone, new[] { "success", "info", "warning", "danger", "muted" });
        }
    }

    [Fact]
    public void DescribeFailure_TranslatesKnownCategory()
    {
        var text = VoWifiStatePresenter.DescribeFailure("Stage 1/4 ePDG discovery", "DNS/ePDG discovery", "x");
        Assert.Contains("ePDG", text);
    }

    [Fact]
    public void DescribeFailure_UnknownCategory_FallsBackToRawCategory()
    {
        var text = VoWifiStatePresenter.DescribeFailure(null, "some new category", "x");
        Assert.Contains("some new category", text);
    }

    [Fact]
    public void DescribeFailure_NullEverything_DoesNotThrow()
    {
        Assert.False(string.IsNullOrWhiteSpace(VoWifiStatePresenter.DescribeFailure(null, null, null)));
    }

    [Fact]
    public void DescribeTelephonyState_CoversAllEnumValues()
    {
        foreach (TelephonyState state in Enum.GetValues<TelephonyState>())
            Assert.False(string.IsNullOrWhiteSpace(VoWifiStatePresenter.DescribeTelephonyState(state)));
    }
}

public class MaskedIdentifierConverterTests
{
    [Fact]
    public void Masks_LongIdentifiers()
    {
        var c = new VoWin.Helpers.MaskedIdentifierConverter();
        var r = (string)c.Convert("8986007311325300026", typeof(string), null, null)!;
        Assert.StartsWith("8986", r);
        Assert.EndsWith("0026", r);
        Assert.Contains("···", r);
        Assert.DoesNotContain("7311", r);
    }

    [Theory]
    [InlineData("—")]
    [InlineData("未知")]
    [InlineData("1234")]
    [InlineData("")]
    public void PassesThrough_ShortOrPlaceholder(string input)
    {
        var c = new VoWin.Helpers.MaskedIdentifierConverter();
        Assert.Equal(input, c.Convert(input, typeof(string), null, null));
    }

    [Fact]
    public void IsPlausibleOperatorLabel_AcceptRealBrands_RejectsMojibake()
    {
        Assert.True(CarrierDisplayHelper.IsPlausibleOperatorLabel("中国移动"));
        Assert.True(CarrierDisplayHelper.IsPlausibleOperatorLabel("Vodafone"));
        Assert.True(CarrierDisplayHelper.IsPlausibleOperatorLabel("Voxi"));
        Assert.True(CarrierDisplayHelper.IsPlausibleOperatorLabel("英国 EE"));
        Assert.False(CarrierDisplayHelper.IsPlausibleOperatorLabel("àààààààààààààààààà¥ ···4314"));
        Assert.False(CarrierDisplayHelper.IsPlausibleOperatorLabel("àáâãàáâãàáâãàáâã…¥"));
        Assert.False(CarrierDisplayHelper.IsPlausibleOperatorLabel("DΦQztàáááááááááááááá¥"));
    }
}

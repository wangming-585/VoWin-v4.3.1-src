using VoSharp.Kernel.Health;
using VoSharp.Kernel.SipGateway;
using VoSharp.Sip;

namespace VoWin.Tests;

// ==================== WatchdogBudget / WatchdogHealthRegistry ====================
public class WatchdogHealthRegistryTests
{
    [Fact]
    public void Publish_NewSlot_CreatesHealthyEntry()
    {
        var reg = new WatchdogHealthRegistry();
        reg.Publish("slot1", HealthVerdict.Healthy, TimeSpan.Zero, null);
        var h = reg.Get("slot1")!;
        Assert.NotNull(h);
        Assert.Equal(HealthVerdict.Healthy, h.Verdict);
        Assert.Equal(0, h.ConsecutiveFailures);
    }

    [Fact]
    public void Publish_Unhealthy_IncrementsConsecutiveFailures()
    {
        var reg = new WatchdogHealthRegistry();
        reg.Publish("s", HealthVerdict.Stale, TimeSpan.FromSeconds(60), null);
        reg.Publish("s", HealthVerdict.Stale, TimeSpan.FromSeconds(70), null);
        reg.Publish("s", HealthVerdict.Healthy, TimeSpan.Zero, null);
        Assert.Equal(0, reg.Get("s")!.ConsecutiveFailures); // 健康清零
        reg.Publish("s", HealthVerdict.Degraded, TimeSpan.FromSeconds(5), null);
        Assert.Equal(1, reg.Get("s")!.ConsecutiveFailures);
    }

    [Fact]
    public void Publish_RaisesHealthChanged()
    {
        var reg = new WatchdogHealthRegistry();
        SlotHealth? last = null;
        reg.HealthChanged += (_, h) => last = h;
        reg.Publish("s", HealthVerdict.Stale, TimeSpan.FromSeconds(9), "数据停滞");
        Assert.NotNull(last);
        Assert.Equal("s", last!.SlotId);
        Assert.Equal(HealthVerdict.Stale, last.Verdict);
    }

    [Fact]
    public void TryConsumeRepair_ExhaustsAfterThreeActions()
    {
        var reg = new WatchdogHealthRegistry();
        for (int i = 0; i < 3; i++)
            Assert.True(reg.TryConsumeRepair("s", WatchdogTier.Slot));
        Assert.False(reg.TryConsumeRepair("s", WatchdogTier.Slot));
        Assert.True(reg.IsBudgetExhausted("s", WatchdogTier.Slot));
    }

    [Fact]
    public void Tiers_HaveIndependentBudgets()
    {
        var reg = new WatchdogHealthRegistry();
        for (int i = 0; i < 3; i++) reg.TryConsumeRepair("s", WatchdogTier.Slot);
        Assert.True(reg.IsBudgetExhausted("s", WatchdogTier.Slot));
        Assert.False(reg.IsBudgetExhausted("s", WatchdogTier.Pool));
        Assert.True(reg.TryConsumeRepair("s", WatchdogTier.Pool));
    }

    [Fact]
    public void Publish_AfterBudgetExhausted_ReportsSuppressed()
    {
        var reg = new WatchdogHealthRegistry();
        for (int i = 0; i < 3; i++) reg.TryConsumeRepair("s", WatchdogTier.Slot);
        reg.Publish("s", HealthVerdict.Stale, TimeSpan.FromSeconds(30), null);
        Assert.Equal(HealthVerdict.Suppressed, reg.Get("s")!.Verdict);
    }

    [Fact]
    public void Reset_RestoresBudgets()
    {
        var reg = new WatchdogHealthRegistry();
        for (int i = 0; i < 3; i++) reg.TryConsumeRepair("s", WatchdogTier.Slot);
        reg.Publish("s", HealthVerdict.Stale, TimeSpan.FromSeconds(1), null);
        reg.SetEscalation("s", EscalationLevel.ToPool);
        reg.Reset("s");
        Assert.False(reg.IsBudgetExhausted("s", WatchdogTier.Slot));
        Assert.Equal(EscalationLevel.None, reg.Get("s")!.Escalation);
        Assert.True(reg.TryConsumeRepair("s", WatchdogTier.Slot));
    }

    [Fact]
    public void RecordRecovery_UpdatesDiagnostics()
    {
        var reg = new WatchdogHealthRegistry();
        reg.RecordRecovery("s", "重启调制解调器");
        var h = reg.Get("s")!;
        Assert.Equal("重启调制解调器", h.LastRecoveryAction);
        Assert.NotNull(h.LastRecoveryTime);
    }

    [Fact]
    public void SetEscalation_OnlyFiresEventOnChange()
    {
        var reg = new WatchdogHealthRegistry();
        int fired = 0;
        reg.HealthChanged += (_, _) => fired++;
        reg.SetEscalation("s", EscalationLevel.ToPool);
        reg.SetEscalation("s", EscalationLevel.ToPool); // 无变化
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Get_UnknownSlot_ReturnsNull() => Assert.Null(new WatchdogHealthRegistry().Get("nope"));

    [Fact]
    public void Forget_RemovesSlot()
    {
        var reg = new WatchdogHealthRegistry();
        reg.Publish("s", HealthVerdict.Healthy, TimeSpan.Zero, null);
        reg.Forget("s");
        Assert.Null(reg.Get("s"));
    }

    [Fact]
    public void Snapshot_ListsAllSlots()
    {
        var reg = new WatchdogHealthRegistry();
        reg.Publish("a", HealthVerdict.Healthy, TimeSpan.Zero, null);
        reg.Publish("b", HealthVerdict.Healthy, TimeSpan.Zero, null);
        Assert.Equal(2, reg.Snapshot().Count);
    }
}

public class WatchdogBudgetTests
{
    [Fact]
    public void IsExhausted_FalseInitially()
        => Assert.False(new WatchdogBudget(TimeSpan.FromMinutes(2), 3).IsExhausted());

    [Fact]
    public void RecordAction_ExhaustsAtLimit()
    {
        var b = new WatchdogBudget(TimeSpan.FromMinutes(2), 2);
        b.RecordAction();
        Assert.False(b.IsExhausted());
        b.RecordAction();
        Assert.True(b.IsExhausted());
    }
}

// ==================== SipDigestAuthenticator ====================
public class SipDigestAuthenticatorTests
{
    private static readonly IReadOnlyDictionary<string, string> Accounts =
        new Dictionary<string, string> { ["alice"] = "secret123" };

    private static SipDigestAuthenticator NewAuth() => new("vowin.local", Accounts);

    [Fact]
    public void CreateChallenge_ContainsDigestFields()
    {
        var c = NewAuth().CreateChallenge();
        Assert.StartsWith("Digest ", c);
        Assert.Contains("realm=\"vowin.local\"", c);
        Assert.Contains("nonce=\"", c);
        Assert.Contains("algorithm=MD5", c);
        Assert.Contains("qop=\"auth\"", c);
    }

    [Fact]
    public void CreateChallenge_NonceUnique()
    {
        var a = NewAuth();
        Assert.NotEqual(a.CreateChallenge(), a.CreateChallenge());
    }

    private static SipMessage MakeRegister(string uri = "sip:vowin.local")
        => new() { Method = "REGISTER", RequestUri = uri };

    private static string BuildAuthHeader(
        SipDigestAuthenticator auth, string challenge, string user, string pass,
        string method = "REGISTER", string uri = "sip:vowin.local", string? nc = null)
    {
        // 从 challenge 解析 nonce
        var nonce = SipDigestAuthenticator.ParseParameters(challenge[7..])["nonce"];
        var realm = SipDigestAuthenticator.ParseParameters(challenge[7..])["realm"];
        var cnonce = "0a1b2c3d";
        nc ??= "00000001";
        static string Md5Hex(string s) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        var ha1 = Md5Hex($"{user}:{realm}:{pass}");
        var ha2 = Md5Hex($"{method}:{uri}");
        var resp = Md5Hex($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");
        return $"Digest username=\"{user}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{resp}\", algorithm=MD5, qop=auth, nc={nc}, cnonce=\"{cnonce}\"";
    }

    [Fact]
    public void Validate_CorrectCredentials_Accepts()
    {
        var auth = NewAuth();
        var ch = auth.CreateChallenge();
        var req = MakeRegister();
        req.SetHeader("Authorization", BuildAuthHeader(auth, ch, "alice", "secret123"));
        Assert.True(auth.Validate(req, out var user));
        Assert.Equal("alice", user);
    }

    [Fact]
    public void Validate_WrongPassword_Rejects()
    {
        var auth = NewAuth();
        var ch = auth.CreateChallenge();
        var req = MakeRegister();
        req.SetHeader("Authorization", BuildAuthHeader(auth, ch, "alice", "wrong"));
        Assert.False(auth.Validate(req, out _));
    }

    [Fact]
    public void Validate_UnknownUser_Rejects()
    {
        var auth = NewAuth();
        var ch = auth.CreateChallenge();
        var req = MakeRegister();
        req.SetHeader("Authorization", BuildAuthHeader(auth, ch, "bob", "x"));
        Assert.False(auth.Validate(req, out _));
    }

    [Fact]
    public void Validate_MissingHeader_Rejects()
    {
        Assert.False(NewAuth().Validate(MakeRegister(), out _));
    }

    [Fact]
    public void Validate_NonceReuseSameNc_Rejects()
    {
        var auth = NewAuth();
        var ch = auth.CreateChallenge();
        var req1 = MakeRegister();
        req1.SetHeader("Authorization", BuildAuthHeader(auth, ch, "alice", "secret123", nc: "00000001"));
        Assert.True(auth.Validate(req1, out _));
        var req2 = MakeRegister();
        req2.SetHeader("Authorization", BuildAuthHeader(auth, ch, "alice", "secret123", nc: "00000001"));
        Assert.False(auth.Validate(req2, out _)); // nc 不递增 → 重放拒绝
    }

    [Fact]
    public void Validate_NonceWithIncreasingNc_Accepts()
    {
        var auth = NewAuth();
        var ch = auth.CreateChallenge();
        var req1 = MakeRegister();
        req1.SetHeader("Authorization", BuildAuthHeader(auth, ch, "alice", "secret123", nc: "00000001"));
        Assert.True(auth.Validate(req1, out _));
        var req2 = MakeRegister();
        req2.SetHeader("Authorization", BuildAuthHeader(auth, ch, "alice", "secret123", nc: "00000002"));
        Assert.True(auth.Validate(req2, out _));
    }

    [Fact]
    public void Validate_UriMismatch_Rejects()
    {
        var auth = NewAuth();
        var ch = auth.CreateChallenge();
        var req = MakeRegister("sip:other.local"); // 请求 URI 与 Authorization 里的不同
        req.SetHeader("Authorization", BuildAuthHeader(auth, ch, "alice", "secret123", uri: "sip:vowin.local"));
        Assert.False(auth.Validate(req, out _));
    }

    [Fact]
    public void ParseParameters_QuotedAndBare()
    {
        var p = SipDigestAuthenticator.ParseParameters("username=\"alice\", realm=test, nc=00000001, qop=auth");
        Assert.Equal("alice", p["username"]);
        Assert.Equal("test", p["realm"]);
        Assert.Equal("00000001", p["nc"]);
        Assert.Equal("auth", p["qop"]);
    }

    [Fact]
    public void ParseParameters_EscapedQuote()
    {
        var p = SipDigestAuthenticator.ParseParameters(@"username=""a\""b"", x=1");
        Assert.Equal("a\"b", p["username"]);
    }
}

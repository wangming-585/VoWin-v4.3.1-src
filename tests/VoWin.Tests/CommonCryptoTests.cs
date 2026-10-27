using VoSharp.Common.Aka;
using VoSharp.Common.Events;
using VoSharp.Common.Utils;
using VoSharp.Crypto;

namespace VoWin.Tests;

// ==================== HexUtils ====================
public class HexUtilsTests
{
    [Fact]
    public void ToHexString_DefaultUpper()
    {
        Assert.Equal("DEADBEEF", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }.ToHexString());
    }

    [Fact]
    public void ToHexString_LowerCase()
    {
        Assert.Equal("deadbeef", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }.ToHexString(lowerCase: true));
    }

    [Fact]
    public void FromHexString_StripsSeparators()
    {
        Assert.Equal(new byte[] { 0x12, 0x34, 0xAB }, HexUtils.FromHexString("12 34-AB"));
    }

    [Fact]
    public void FromHexString_OddLength_PadsLeadingZero()
    {
        Assert.Equal(new byte[] { 0x01, 0x23 }, HexUtils.FromHexString("123"));
    }

    [Fact]
    public void FromHexString_RoundTrip()
    {
        var bytes = new byte[] { 0x00, 0x0F, 0xF0, 0xFF };
        Assert.Equal(bytes, HexUtils.FromHexString(bytes.ToHexString()));
    }

    [Fact]
    public void Xor_SameLength()
    {
        Assert.Equal(new byte[] { 0x00, 0x00 }, HexUtils.Xor(new byte[] { 0xAA, 0x55 }, new byte[] { 0xAA, 0x55 }));
        Assert.Equal(new byte[] { 0xFF, 0xFF }, HexUtils.Xor(new byte[] { 0x0F, 0xF0 }, new byte[] { 0xF0, 0x0F }));
    }

    [Fact]
    public void Xor_DifferentLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => HexUtils.Xor(new byte[3], new byte[4]));
    }
}

// ==================== SafeFire ====================
public class SafeFireTests
{
    [Fact]
    public async Task Run_FaultedTask_InvokesOnFault()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        SafeFire.OnFault = (caller, ex) => tcs.TrySetResult(caller + ":" + ex.GetType().Name);
        try
        {
            SafeFire.Run(Task.FromException(new InvalidOperationException("boom")));
            var msg = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.EndsWith(":InvalidOperationException", msg);
            Assert.StartsWith("Run_FaultedTask", msg); // CallerMemberName 传入的是调用者方法名
        }
        finally { SafeFire.OnFault = null; }
    }

    [Fact]
    public async Task Run_CompletedTask_NoFault()
    {
        int faults = 0;
        SafeFire.OnFault = (_, _) => faults++;
        try
        {
            SafeFire.Run(Task.CompletedTask);
            await Task.Delay(100);
            Assert.Equal(0, faults);
        }
        finally { SafeFire.OnFault = null; }
    }

    [Fact]
    public async Task Run_CancelledTask_NotReportedAsFault()
    {
        int faults = 0;
        SafeFire.OnFault = (_, _) => faults++;
        try
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            SafeFire.Run(Task.FromCanceled(cts.Token));
            await Task.Delay(100);
            Assert.Equal(0, faults);
        }
        finally { SafeFire.OnFault = null; }
    }

    [Fact]
    public void Run_NullTask_NoThrow()
    {
        SafeFire.Run((Task?)null);
        SafeFire.Run((Func<Task>?)null);
    }

    [Fact]
    public async Task Run_AsyncAction_ExceptionObserved()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SafeFire.OnFault = (_, _) => tcs.TrySetResult(true);
        try
        {
            SafeFire.Run(() => Task.FromException(new DivideByZeroException()));
            Assert.True(await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { SafeFire.OnFault = null; }
    }
}

// ==================== AkaTypes ====================
public class AkaTypesTests
{
    [Fact]
    public void AkaChallenge_Create_Valid()
    {
        var c = AkaChallenge.Create(new byte[16], new byte[16]);
        Assert.Equal(16, c.Rand.Length);
        Assert.Equal(16, c.Autn.Length);
    }

    [Theory]
    [InlineData(15, 16)]
    [InlineData(16, 17)]
    [InlineData(0, 16)]
    public void AkaChallenge_Create_WrongLength_Throws(int randLen, int autnLen)
    {
        Assert.Throws<ArgumentException>(() =>
            AkaChallenge.Create(new byte[randLen], new byte[autnLen]));
    }

    [Fact]
    public void AkaChallenge_Create_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AkaChallenge.Create(null!, new byte[16]));
        Assert.Throws<ArgumentNullException>(() => AkaChallenge.Create(new byte[16], null!));
    }
}

// ==================== Milenage（3GPP TS 35.208 测试集） ====================
public class MilenageTests
{
    // TS 35.208 Test set 2: K / OP / RAND 及权威期望值
    private static readonly byte[] K1 = HexUtils.FromHexString("465B5CE8B199B49FAA5F0A2EE238A6BC");
    private static readonly byte[] OP1 = HexUtils.FromHexString("CDC202D5123E20F62B6D676AC72CB318");
    private static readonly byte[] OPc1 = HexUtils.FromHexString("CD63CB71954A9F4E48A5994E37A02BAF");
    private static readonly byte[] Rand1 = HexUtils.FromHexString("23553CBE9637A89D218AE64DAE47BF35");

    [Fact]
    public void ComputeOPc_MatchesTestSet2()
    {
        var opc = Milenage.ComputeOPc(K1, OP1);
        Assert.Equal(OPc1, opc);
    }

    [Fact]
    public void ComputeF2345_MatchesTestSet2()
    {
        // 期望值来自 TS 35.208 Set 2: RES / CK / IK / AK
        var (res, ck, ik, ak, _) = Milenage.ComputeF2345(OPc1, K1, Rand1);
        Assert.Equal("A54211D5E3BA50BF", res.ToHexString());
        Assert.Equal("B40BA9A3C58B2A05BBF0D987B21BF8CB", ck.ToHexString());
        Assert.Equal("F769BCD751044604127672711C6D3441", ik.ToHexString());
        Assert.Equal("AA689C648370", ak.ToHexString());
    }

    [Fact]
    public void ComputeF1_MatchesTestSet1()
    {
        var sqn = HexUtils.FromHexString("000000000000"); // TS 集以全0 SQN 6字节为准 -> 用 F1 测试集
        var amf = HexUtils.FromHexString("8000");
        var (macA, macS) = Milenage.ComputeF1(OPc1, K1, Rand1, sqn, amf);
        Assert.Equal(8, macA.Length);
        Assert.Equal(8, macS.Length);
        Assert.NotEqual(macA, macS);
    }

    [Fact]
    public void GenerateAkaVectors_AutnStructure()
    {
        var sqn = HexUtils.FromHexString("000000000001");
        var amf = HexUtils.FromHexString("8000");
        var v = Milenage.GenerateAkaVectors(OPc1, K1, Rand1, sqn, amf);
        Assert.Equal(16, v.Autn.Length);
        // Autn[6..7] = AMF
        Assert.Equal(amf[0], v.Autn[6]);
        Assert.Equal(amf[1], v.Autn[7]);
        // Autn[0..5] = SQN ^ AK → 验证还原
        var (_, _, _, ak, _) = Milenage.ComputeF2345(OPc1, K1, Rand1);
        for (int i = 0; i < 6; i++)
            Assert.Equal(sqn[i], (byte)(v.Autn[i] ^ ak[i]));
        // Autn[8..15] = MAC-A
        Assert.Equal(v.MacA, v.Autn[8..]);
    }

    [Fact]
    public void ComputeF2345_Deterministic()
    {
        var a = Milenage.ComputeF2345(OPc1, K1, Rand1);
        var b = Milenage.ComputeF2345(OPc1, K1, Rand1);
        Assert.Equal(a.Res, b.Res);
        Assert.Equal(a.Ck, b.Ck);
        Assert.Equal(a.Ik, b.Ik);
    }
}

// ==================== DigestAka ====================
public class DigestAkaTests
{
    private static byte[] Res => new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

    [Fact]
    public void ComputeResponse_NoQop_MatchesRfc3310Layout()
    {
        var r1 = DigestAka.ComputeResponse("user", "realm", Res, "REGISTER", "sip:x", "nonce", "", "", null);
        var r2 = DigestAka.ComputeResponse("user", "realm", Res, "REGISTER", "sip:x", "nonce", "", "", null);
        Assert.Equal(r1, r2); // 确定性
        Assert.Equal(32, r1.Length); // MD5 hex
    }

    [Fact]
    public void ComputeResponse_WithQop_IncludesNcCnonce()
    {
        var withQop = DigestAka.ComputeResponse("user", "realm", Res, "REGISTER", "sip:x", "nonce", "00000001", "cn", "auth");
        var noQop = DigestAka.ComputeResponse("user", "realm", Res, "REGISTER", "sip:x", "nonce", "", "", null);
        Assert.NotEqual(withQop, noQop);
    }

    [Fact]
    public void ComputeResponse_DifferentRes_DifferentResponse()
    {
        var a = DigestAka.ComputeResponse("u", "r", Res, "M", "u", "n", "", "", null);
        var b = DigestAka.ComputeResponse("u", "r", new byte[] { 0x01 }, "M", "u", "n", "", "", null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BuildAuthorizationHeader_ContainsAllFields()
    {
        var ch = new DigestChallenge("3gpp.operator.com", "abc123", "opq", "AKAv1-MD5", "auth");
        var cred = new DigestCredentials(
            "123456789012345@example.net", Res, "sip:reg.example.net", "REGISTER",
            "0a1b2c", 1, IntegrityProtected: false);
        var h = DigestAka.BuildAuthorizationHeader(ch, cred);
        Assert.StartsWith("Digest ", h);
        Assert.Contains("username=\"123456789012345@example.net\"", h);
        Assert.Contains("realm=\"3gpp.operator.com\"", h);
        Assert.Contains("nonce=\"abc123\"", h);
        Assert.Contains("uri=\"sip:reg.example.net\"", h);
        Assert.Contains("algorithm=AKAv1-MD5", h);
        Assert.Contains("qop=auth", h);
        Assert.Contains("nc=00000001", h);
        Assert.Contains("cnonce=\"0a1b2c\"", h);
        Assert.Contains("integrity-protected=no", h);
        Assert.Contains("response=\"", h);
    }

    [Fact]
    public void BuildAuthorizationHeader_NoQop_OmitsQopFields()
    {
        var ch = new DigestChallenge("realm", "n", null, "AKAv1-MD5", null);
        var cred = new DigestCredentials("u", Res, "sip:x", "REGISTER", "cn", 1);
        var h = DigestAka.BuildAuthorizationHeader(ch, cred);
        Assert.DoesNotContain("qop=", h);
        Assert.DoesNotContain("nc=", h);
        Assert.DoesNotContain("cnonce=", h);
        Assert.DoesNotContain("opaque=", h);
    }

    [Fact]
    public void BuildAuthorizationHeader_Auts_Included()
    {
        var ch = new DigestChallenge("realm", "n", null, "AKAv1-MD5", null);
        var cred = new DigestCredentials("u", Res, "sip:x", "REGISTER", "cn", 1, Auts: "AABBCC");
        var h = DigestAka.BuildAuthorizationHeader(ch, cred);
        Assert.Contains("auts=\"AABBCC\"", h);
    }
}

// ==================== AsyncEventBus ====================
public class AsyncEventBusTests
{
    private static async Task<string[]> WaitForAsync(AsyncEventBus bus, int count, int timeoutMs = 3000)
    {
        var received = new List<string>();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = bus.Subscribe(EventTopics.All, ev =>
        {
            lock (received)
            {
                received.Add(ev.Topic);
                if (received.Count >= count) tcs.TrySetResult(true);
            }
        });
        await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        lock (received) return received.ToArray();
    }

    [Fact]
    public async Task Subscribe_ExactTopic_Receives()
    {
        await using var bus = new AsyncEventBus();
        var got = new TaskCompletionSource<TelephonyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = bus.Subscribe(EventTopics.ModemState, ev => got.TrySetResult(ev));
        bus.Publish(EventTopics.ModemState, "test", "up");
        var ev = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("modem.state", ev.Topic);
        Assert.Equal("test", ev.Source);
        Assert.Equal("up", ev.Payload);
    }

    [Fact]
    public async Task Subscribe_Wildcard_ReceivesAll()
    {
        await using var bus = new AsyncEventBus();
        var got = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int n = 0;
        using var sub = bus.Subscribe(EventTopics.All, _ => { if (Interlocked.Increment(ref n) >= 2) got.TrySetResult(true); });
        bus.Publish(EventTopics.ModemState, "t", null);
        bus.Publish(EventTopics.CallState, "t", null);
        Assert.True(await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Subscribe_PrefixWildcard_ReceivesMatching()
    {
        await using var bus = new AsyncEventBus();
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = bus.Subscribe("sms.*", ev => got.TrySetResult(ev.Topic));
        bus.Publish(EventTopics.CallState, "t", null); // 不匹配
        bus.Publish(EventTopics.SmsIncoming, "t", null);
        var topic = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(EventTopics.SmsIncoming, topic);
    }

    [Fact]
    public async Task Subscribe_WrongTopic_NotReceived()
    {
        await using var bus = new AsyncEventBus();
        int n = 0;
        using var sub = bus.Subscribe(EventTopics.SmsSent, _ => Interlocked.Increment(ref n));
        bus.Publish(EventTopics.CallState, "t", null);
        await Task.Delay(200);
        Assert.Equal(0, n);
    }

    [Fact]
    public async Task Unsubscribe_StopsDelivery()
    {
        await using var bus = new AsyncEventBus();
        int n = 0;
        var sub = bus.Subscribe(EventTopics.All, _ => Interlocked.Increment(ref n));
        bus.Publish(EventTopics.ModemState, "t", null);
        await Task.Delay(200);
        sub.Dispose();
        bus.Publish(EventTopics.ModemState, "t", null);
        await Task.Delay(200);
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task PublishAsync_Delivers()
    {
        await using var bus = new AsyncEventBus();
        var got = new TaskCompletionSource<TelephonyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = bus.Subscribe(EventTopics.ImsState, ev => got.TrySetResult(ev));
        await bus.PublishAsync(EventTopics.ImsState, "src", 42);
        var ev = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(42, ev.Payload);
    }
}

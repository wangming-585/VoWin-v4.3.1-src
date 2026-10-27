using VoWin.Helpers;
using System.IO;
using VoWin.ViewModels.Pages;
using VoWin.Services;

namespace VoWin.Tests;

// ==================== TimestampDisplayHelper ====================
public class TimestampDisplayHelperTests
{
    [Fact]
    public void ToUtcStorageTime_Kinds()
    {
        var utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc, TimestampDisplayHelper.ToUtcStorageTime(utc));
        var local = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        Assert.Equal(DateTimeKind.Utc, TimestampDisplayHelper.ToUtcStorageTime(local).Kind);
    }

    [Fact]
    public void ToLocalDisplayTime_RoundTrips()
    {
        var utc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var local = TimestampDisplayHelper.ToLocalDisplayTime(utc);
        Assert.Equal(DateTimeKind.Local, local.Kind);
        Assert.Equal(utc, local.ToUniversalTime());
    }

    [Fact]
    public void TryParseStoredUtc_RoundTrip()
    {
        var utc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(TimestampDisplayHelper.TryParseStoredUtc(utc.ToString("o"), out var parsed));
        Assert.Equal(utc, parsed);
    }

    [Fact]
    public void TryParseStoredUtc_Invalid()
    {
        Assert.False(TimestampDisplayHelper.TryParseStoredUtc("not-a-date", out _));
        Assert.False(TimestampDisplayHelper.TryParseStoredUtc(null, out _));
    }

    [Fact]
    public void NormalizeIncomingNetworkTime_PlausibleTimestamp_Unchanged()
    {
        var received = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var ts = received.AddMinutes(-1);
        Assert.Equal(ts, TimestampDisplayHelper.NormalizeIncomingNetworkTime(ts, received));
    }

    [Fact]
    public void NormalizeIncomingNetworkTime_FarFuture_Reinterpreted()
    {
        var received = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        // 声称 GMT+0 但值在未来 8 小时 → 应重释为本地墙钟
        var future = received.AddHours(8);
        var result = TimestampDisplayHelper.NormalizeIncomingNetworkTime(future, received);
        Assert.True(result <= received + TimeSpan.FromMinutes(5));
    }
}

// ==================== DiagnosticLogRedactor ====================
public class DiagnosticLogRedactorTests
{
    [Fact]
    public void Redact_Iccid()
    {
        var out1 = DiagnosticLogRedactor.Redact("卡号: 89860012345678901234 已插入");
        Assert.DoesNotContain("89860012345678901234", out1);
        Assert.Contains("redacted", out1);
    }

    [Fact]
    public void Redact_SipUriUser()
    {
        var o = DiagnosticLogRedactor.Redact("REGISTER sip:+8613800138000@ims.example.net SIP/2.0");
        Assert.DoesNotContain("+8613800138000", o);
        Assert.Contains("sip:", o);
    }

    [Fact]
    public void Redact_NonceValue()
    {
        var o = DiagnosticLogRedactor.Redact("nonce=\"abcdef1234567890\"");
        Assert.DoesNotContain("abcdef1234567890", o);
    }

    [Fact]
    public void Redact_AuthorizationHeader()
    {
        var o = DiagnosticLogRedactor.Redact("Authorization: Digest username=\"1001\", response=\"deadbeef\"");
        Assert.DoesNotContain("deadbeef", o); // response 值脱敏；username 非密钥，按设计保留
    }

    [Fact]
    public void Redact_AtsimCommand()
    {
        var o = DiagnosticLogRedactor.Redact("AT+CSIM=14,\"00A4040010\"");
        Assert.DoesNotContain("00A4040010", o);
    }

    [Fact]
    public void Redact_ProxyPassword()
    {
        var o = DiagnosticLogRedactor.Redact("socks5://user:p@ssw0rd@1.2.3.4:1080");
        Assert.DoesNotContain("p@ssw0rd", o);
    }

    [Fact]
    public void Redact_LabeledImsi()
    {
        var o = DiagnosticLogRedactor.Redact("imsi=460001234567890");
        Assert.DoesNotContain("460001234567890", o);
    }

    [Fact]
    public void Redact_EmptyAndNull()
    {
        Assert.Equal(string.Empty, DiagnosticLogRedactor.Redact(""));
        Assert.Equal(string.Empty, DiagnosticLogRedactor.Redact(null));
    }

    [Fact]
    public void Redact_BenignTextUntouched()
    {
        const string text = "IKE_SA_INIT 发送成功，SPI=0x1234567890abcdef";
        Assert.Equal(text, DiagnosticLogRedactor.Redact(text));
    }
}

// ==================== InMemoryPcmPipe ====================
public class InMemoryPcmPipeTests
{
    [Fact]
    public void WriteThenRead_FrameIntact()
    {
        using var pipe = new InMemoryPcmPipe();
        pipe.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
        var buf = new byte[4];
        Assert.Equal(4, pipe.Read(buf, 0, 4));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf);
    }

    [Fact]
    public void Read_SpanningTwoFrames()
    {
        using var pipe = new InMemoryPcmPipe();
        pipe.Write(new byte[] { 1, 2 }, 0, 2);
        pipe.Write(new byte[] { 3, 4, 5 }, 0, 3);
        var buf = new byte[5];
        int total = 0;
        while (total < 5)
            total += pipe.Read(buf, total, 5 - total);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buf);
    }

    [Fact]
    public void Write_PartialBuffer_CopiesRange()
    {
        using var pipe = new InMemoryPcmPipe();
        pipe.Write(new byte[] { 9, 9, 7, 8, 9, 9 }, 2, 2);
        var buf = new byte[2];
        pipe.Read(buf, 0, 2);
        Assert.Equal(new byte[] { 7, 8 }, buf);
    }

    [Fact]
    public void CompleteWriter_ReadReturnsZero()
    {
        using var pipe = new InMemoryPcmPipe();
        pipe.CompleteWriter();
        Assert.Equal(0, pipe.Read(new byte[4], 0, 4));
    }

    [Fact]
    public void Read_AfterDrainThenComplete_ReturnsZero()
    {
        using var pipe = new InMemoryPcmPipe();
        pipe.Write(new byte[] { 1 }, 0, 1);
        Assert.Equal(1, pipe.Read(new byte[1], 0, 1));
        pipe.CompleteWriter();
        Assert.Equal(0, pipe.Read(new byte[1], 0, 1));
    }

    [Fact]
    public async Task Read_BlocksUntilWrite()
    {
        using var pipe = new InMemoryPcmPipe();
        var readTask = Task.Run(() => pipe.Read(new byte[2], 0, 2));
        Assert.False(readTask.IsCompleted); // 阻塞中
        await Task.Delay(100);
        pipe.Write(new byte[] { 5, 6 }, 0, 2);
        Assert.Equal(2, await readTask.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Write_ZeroCount_NoOp()
    {
        using var pipe = new InMemoryPcmPipe();
        pipe.Write(new byte[4], 0, 0);
        pipe.CompleteWriter();
        Assert.Equal(0, pipe.Read(new byte[1], 0, 1));
    }

    [Fact]
    public void Read_ZeroCount_ReturnsZero()
    {
        using var pipe = new InMemoryPcmPipe();
        Assert.Equal(0, pipe.Read(new byte[4], 0, 0));
    }

    [Fact]
    public void Caps_And_UnsupportedOps()
    {
        using var pipe = new InMemoryPcmPipe();
        Assert.True(pipe.CanRead);
        Assert.True(pipe.CanWrite);
        Assert.False(pipe.CanSeek);
        Assert.Throws<NotSupportedException>(() => pipe.Length);
        Assert.Throws<NotSupportedException>(() => pipe.Seek(0, SeekOrigin.Begin));
        Assert.Equal(0, pipe.Position);
        pipe.Flush(); // no-throw
    }

    [Fact]
    public void Dispose_CompletesWriter()
    {
        var pipe = new InMemoryPcmPipe();
        pipe.Dispose();
        Assert.Equal(0, pipe.Read(new byte[1], 0, 1));
    }
}


// ==================== KeepAlivePolicy 保活熔断 ====================
public class KeepAlivePolicyTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SelfRestart_Allowed_WhenFewRecentStamps()
    {
        var stamps = new[] { Now.AddMinutes(-1), Now.AddMinutes(-2) };
        Assert.True(KeepAlivePolicy.ShouldAllowSelfRestart(stamps, Now));
    }

    [Fact]
    public void SelfRestart_Blocked_AfterThreeInFiveMinutes()
    {
        var stamps = new[] { Now.AddMinutes(-1), Now.AddMinutes(-2), Now.AddMinutes(-3) };
        Assert.False(KeepAlivePolicy.ShouldAllowSelfRestart(stamps, Now));
    }

    [Fact]
    public void SelfRestart_OldStampsOutsideWindowIgnored()
    {
        var stamps = new[] { Now.AddMinutes(-6), Now.AddMinutes(-10), Now.AddMinutes(-20), Now.AddMinutes(-1) };
        Assert.True(KeepAlivePolicy.ShouldAllowSelfRestart(stamps, Now)); // 窗口外3条不计，仅窗口内1条
    }

    [Fact]
    public void UiCircuitBreaker_TriggersAtThreshold()
    {
        var q = new Queue<DateTime>();
        for (var i = 0; i < 19; i++) q.Enqueue(Now.AddSeconds(-30));
        Assert.False(KeepAlivePolicy.ShouldTriggerUiCircuitBreaker(q, Now));
        q.Enqueue(Now);
        Assert.True(KeepAlivePolicy.ShouldTriggerUiCircuitBreaker(q, Now));
    }

    [Fact]
    public void UiCircuitBreaker_PrunesOldStamps()
    {
        var q = new Queue<DateTime>();
        for (var i = 0; i < 30; i++) q.Enqueue(Now.AddMinutes(-5)); // 全部过期
        Assert.False(KeepAlivePolicy.ShouldTriggerUiCircuitBreaker(q, Now));
        Assert.Empty(q); // 过期项已被清理
    }

    [Fact]
    public void ParseRestartStamps_ToleratesBadLines()
    {
        var parsed = KeepAlivePolicy.ParseRestartStamps(
            new[] { Now.AddMinutes(-1).ToString("o"), "garbage", "", Now.AddMinutes(-9).ToString("o") }, Now);
        Assert.Single(parsed); // 坏行与窗口外行都被过滤
    }
}


// ==================== DashboardViewModel.FormatUptime ====================
public class DashboardUptimeTests
{
    [Fact]
    public void FormatUptime_ZeroSeconds()
    {
        var s = DashboardViewModel.FormatUptime(DateTime.UtcNow, isConnected: true);
        Assert.StartsWith("已保持连接 ", s);
        Assert.EndsWith("00:00:00", s);
    }

    [Fact]
    public void FormatUptime_CrossesHourBoundary()
    {
        var s = DashboardViewModel.FormatUptime(DateTime.UtcNow.AddMinutes(-90), isConnected: true);
        Assert.Matches(@"已保持连接 01:30:0\d", s);
    }

    [Fact]
    public void FormatUptime_MultiDayIncludesDays()
    {
        var s = DashboardViewModel.FormatUptime(DateTime.UtcNow.AddHours(-50), isConnected: true);
        Assert.StartsWith("已保持连接 2 天 ", s);
    }

    [Fact]
    public void FormatUptime_EmptyWhenNotConnectedOrNoTimestamp()
    {
        Assert.Equal(string.Empty, DashboardViewModel.FormatUptime(DateTime.UtcNow, isConnected: false));
        Assert.Equal(string.Empty, DashboardViewModel.FormatUptime(null, isConnected: true));
    }

    [Fact]
    public void ResolvePhase_ImsRegistered_MapsToConnected()
    {
        // 状态与 ConnectedAt 一致性：连接态下 FormatUptime 必须给出非空展示
        var phase = VoWin.Helpers.VoWifiStatePresenter.ResolvePhase(
            VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered,
            anySlotPresent: true, simReady: true, userStopped: false);
        Assert.Equal(VoWin.Helpers.VoWifiStatePresenter.DashboardPhase.Connected, phase);
        var uptime = DashboardViewModel.FormatUptime(DateTime.UtcNow.AddSeconds(5), isConnected: phase == VoWin.Helpers.VoWifiStatePresenter.DashboardPhase.Connected);
        Assert.StartsWith("已保持连接 ", uptime);
    }
}

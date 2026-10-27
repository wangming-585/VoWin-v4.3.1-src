using VoSharp.Telephony.Mmi;
using VoSharp.Telephony.Sms;
using System.IO;
using VoWin.Models;
using VoWin.Services;

namespace VoWin.Tests;

public class Gsm7AlphabetTests
{
    [Fact]
    public void CanEncode_BasicAndExtension_True()
    {
        Assert.True(Gsm7Alphabet.CanEncode("Hello, World!"));
        Assert.True(Gsm7Alphabet.CanEncode("price 10€"));
        Assert.True(Gsm7Alphabet.CanEncode(""));
        Assert.True(Gsm7Alphabet.CanEncode("@£$¥èéùìòÇ"));
    }

    [Fact]
    public void CanEncode_UnsupportedChinese_False()
    {
        Assert.False(Gsm7Alphabet.CanEncode("你好世界"));
        Assert.False(Gsm7Alphabet.CanEncode("中文"));
    }

    [Fact]
    public void EncodeToSeptets_ExtensionCharacter_ExpandsEscape()
    {
        var septets = Gsm7Alphabet.EncodeToSeptets("€");
        Assert.Equal(new byte[] { 0x1B, 0x65 }, septets);
    }

    [Fact]
    public void EncodeToSeptets_Unsupported_FallsBackToQuestionMark()
    {
        var septets = Gsm7Alphabet.EncodeToSeptets("中");
        Assert.Equal(new byte[] { 0x3F }, septets);
    }

    [Fact]
    public void DecodeSeptets_UnknownExtension_ThrowsOrSpaces()
    {
        // 0x1B 后面是未知扩展码 -> 按空格处理
        var decoded = Gsm7Alphabet.DecodeSeptets(new byte[] { 0x1B, 0x7F });
        Assert.Equal(" ", decoded);
        // 结尾孤立 escape -> 空格
        var decoded2 = Gsm7Alphabet.DecodeSeptets(new byte[] { 0x1B });
        Assert.Equal(" ", decoded2);
    }

    [Fact]
    public void DecodeSeptets_KnownExtension_Decodes()
    {
        var decoded = Gsm7Alphabet.DecodeSeptets(new byte[] { 0x1B, 0x65 });
        Assert.Equal("€", decoded);
    }

    [Fact]
    public void PackUnpack_RoundTrip_MultipleSeptets()
    {
        var septets = new byte[] { 65, 66, 67, 68, 69 };
        var packed = Gsm7Alphabet.PackSeptets(septets, 0);
        var unpacked = Gsm7Alphabet.UnpackSeptets(packed, septets.Length, 0);
        Assert.Equal(septets, unpacked);
    }

    [Fact]
    public void EncodeDecode7Bit_RoundTrip_Ascii()
    {
        const string text = "Hello, VoWin SMS! 123";
        var packed = Gsm7Alphabet.Encode7Bit(text);
        var decoded = Gsm7Alphabet.Decode7Bit(packed, Gsm7Alphabet.EncodeToSeptets(text).Length);
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void SplitSeptets_PreservesEscapePair()
    {
        // 构造含 escape 对的长串，确保边界不拆散 0x1B+code
        var septets = new List<byte>();
        for (var i = 0; i < 20; i++)
        {
            septets.Add(0x1B);
            septets.Add(0x65);
        }
        var arr = septets.ToArray();
        var chunks = Gsm7Alphabet.SplitSeptets(arr, 7);
        // 任意 chunk 结尾不能是孤立 escape
        foreach (var chunk in chunks)
        {
            if (chunk.Length > 0)
                Assert.NotEqual(0x1B, chunk[^1]);
        }
        Assert.Equal(arr.Length, chunks.Sum(c => c.Length));
    }
}

public class SmsPduTests
{
    [Fact]
    public void NormalizeRecipient_StripsNonDigits_KeepsPlus()
    {
        Assert.Equal("+8613800138000", SmsPdu.NormalizeRecipient(" +86 138-0013 8000 "));
        Assert.Equal("13800138000", SmsPdu.NormalizeRecipient("138-0013-8000"));
        Assert.Equal("10086", SmsPdu.NormalizeRecipient("100 86"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    public void NormalizeRecipient_Invalid_Throws(string recipient)
    {
        Assert.Throws<ArgumentException>(() => SmsPdu.NormalizeRecipient(recipient));
    }

    [Fact]
    public void PrepareSubmitParts_SingleGsm7()
    {
        var parts = SmsPdu.PrepareSubmitParts("+8613800138000", "Hello", concatReference: 1);
        Assert.Single(parts);
        Assert.Equal(1, parts[0].PartNumber);
        Assert.Equal(1, parts[0].TotalParts);
        Assert.Equal(SmsEncoding.Gsm7Bit, parts[0].Encoding);
        Assert.True(parts[0].TpduLength > 0);
    }

    [Fact]
    public void PrepareSubmitParts_ConcatenatedGsm7()
    {
        var text = new string('A', 200);
        var parts = SmsPdu.PrepareSubmitParts("+8613800138000", text);
        Assert.True(parts.Count >= 2);
        Assert.Equal(parts[0].TotalParts, parts.Count);
        Assert.Equal(1, parts[0].PartNumber);
        Assert.Equal(parts.Count, parts[^1].PartNumber);
        Assert.NotNull(parts[0].ConcatReference);
        Assert.Equal(parts[0].ConcatReference, parts[1].ConcatReference);
    }

    [Fact]
    public void PrepareSubmitParts_Ucs2_NonGsm7()
    {
        var parts = SmsPdu.PrepareSubmitParts("+8613800138000", "你好世界");
        Assert.Single(parts);
        Assert.Equal(SmsEncoding.Ucs2, parts[0].Encoding);
    }

    [Theory]
    [InlineData(0x00, "delivered")]
    [InlineData(0x20, "temporary_error")]
    [InlineData(0x55, "permanent_error")]
    [InlineData(0x60, "temporary_error_no_retry")]
    [InlineData(0x90, "reserved")]
    public void FormatDeliveryStatus_Categories(byte code, string expected)
    {
        Assert.Equal(expected, SmsPdu.FormatDeliveryStatus(code));
    }

    [Fact]
    public void DecodeStatusReport_SimpleHex_ParsesFields()
    {
        // SCA(00) + MTI=02 + MR=01 + RA(07 81 214365F7) + SCTS(7) + DT(7) + ST(00)
        var pdu = "00" + "02" + "01" + "07" + "81" + "214365F7" +
                  "20051520304100" + "20051520304101" + "00";
        var report = SmsPdu.DecodeStatusReportPdu(pdu);
        Assert.Equal(1, report.MessageReference);
        Assert.Equal("1234567", report.Recipient);
        Assert.Equal(0, report.StatusCode);
        Assert.Equal("delivered", report.DeliveryStatus);
    }

    [Fact]
    public void DecodePdu_TooShort_Throws()
    {
        Assert.Throws<ArgumentException>(() => SmsPdu.DecodePdu("00"));
        Assert.Throws<ArgumentException>(() => SmsPdu.DecodePdu(""));
    }

    [Fact]
    public void DecodePdu_StatusReport_MapsToIncomingSms()
    {
        var pdu = "00" + "02" + "01" + "07" + "81" + "214365F7" +
                  "20051520304100" + "20051520304101" + "00";
        var incoming = SmsPdu.DecodePdu(pdu);
        Assert.True(incoming.IsStatusReport);
        Assert.NotNull(incoming.StatusReport);
        Assert.True(incoming.Text.Contains("Delivery Report", StringComparison.OrdinalIgnoreCase));
    }
}

public class MmiParserTests
{
    [Fact]
    public void Parse_IMEIQuery()
    {
        var cmd = MmiParser.Parse("*#06#");
        Assert.NotNull(cmd);
        Assert.Equal("IMEI", cmd.Service);
        Assert.Equal("QUERY", cmd.Action);
        Assert.Null(cmd.Target);
    }

    [Fact]
    public void Parse_ForwardingActivate_WithTarget()
    {
        var cmd = MmiParser.Parse("*21*13800138000#");
        Assert.NotNull(cmd);
        Assert.Equal("CallForwardingUnconditional", cmd.Service);
        Assert.Equal("ACTIVATE", cmd.Action);
        Assert.Equal("13800138000", cmd.Target);
    }

    [Theory]
    [InlineData("#67#", "CallForwardingBusy", "DEACTIVATE")]
    [InlineData("##43#", "CallWaiting", "ERASE")]
    [InlineData("#31#", "CLIR", "DEACTIVATE")]
    [InlineData("*30#", "CLIP", "ACTIVATE")]
    public void Parse_DeactivateAndErase_Variants(string input, string service, string action)
    {
        var cmd = MmiParser.Parse(input);
        Assert.NotNull(cmd);
        Assert.Equal(service, cmd.Service);
        Assert.Equal(action, cmd.Action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("*#abcd#")]
    [InlineData("*21")]
    [InlineData("*#06")]
    [InlineData("ABCD")]
    public void Parse_Invalid_ReturnsNull(string input)
    {
        Assert.Null(MmiParser.Parse(input));
    }

    [Fact]
    public void Parse_UnknownService_GeneratesPlaceholder()
    {
        var cmd = MmiParser.Parse("*42*99#");
        Assert.NotNull(cmd);
        Assert.Equal("Service_42", cmd.Service);
        Assert.Equal("ACTIVATE", cmd.Action);
        Assert.Equal("99", cmd.Target);
    }
}

public class SmsReassemblerTests
{
    private static IncomingSms Part(string sender, string text, int total, int seq, int reference = 7) =>
        new(sender, text, DateTime.UtcNow, Concat: new SmsConcatInfo(reference, total, seq));

    [Fact]
    public void SinglePart_ReturnsImmediately()
    {
        var r = new SmsReassembler();
        var part = Part("138", "single", total: 1, seq: 1);
        var result = r.ProcessIncomingPart(part);
        Assert.NotNull(result);
        Assert.Equal("single", result.Text);
    }

    [Fact]
    public void OutOfOrder_ReassemblesWhenComplete()
    {
        var r = new SmsReassembler();
        Assert.Null(r.ProcessIncomingPart(Part("138", "world", 2, 2)));
        var result = r.ProcessIncomingPart(Part("138", "hello ", 2, 1));
        Assert.NotNull(result);
        Assert.Equal("hello world", result.Text);
        Assert.Equal(2, result.Concat!.Total);
    }

    [Fact]
    public void DuplicateSegment_Overwrites_DoesNotBreakAssembly()
    {
        var r = new SmsReassembler();
        Assert.Null(r.ProcessIncomingPart(Part("138", "a", 2, 1)));
        Assert.Null(r.ProcessIncomingPart(Part("138", "b", 2, 1))); // 重复段覆盖
        var result = r.ProcessIncomingPart(Part("138", "c", 2, 2));
        Assert.NotNull(result);
        Assert.Equal("bc", result.Text);
    }

    [Fact]
    public void InvalidSequence_ReturnsPartDirectly()
    {
        var r = new SmsReassembler();
        var part = Part("138", "oob", 2, 5);
        Assert.Same(part, r.ProcessIncomingPart(part));
    }

    [Fact]
    public void TotalBeyond255_ReturnsPartDirectly()
    {
        var r = new SmsReassembler();
        var part = Part("138", "big", 256, 3);
        Assert.Same(part, r.ProcessIncomingPart(part));
    }

    [Fact]
    public void StaleCleanup_RemovesGroup()
    {
        var r = new SmsReassembler();
        Assert.Null(r.ProcessIncomingPart(Part("138", "seg1", 2, 1)));
        r.CleanupStaleFragments(TimeSpan.FromMilliseconds(-1));
        // 组被清理后，第二段会新开组仍不完整
        Assert.Null(r.ProcessIncomingPart(Part("138", "seg2", 2, 2)));
    }

    [Fact]
    public void DifferentSender_IsolatedPerGroup()
    {
        var r = new SmsReassembler();
        Assert.Null(r.ProcessIncomingPart(Part("A", "s1", 2, 1)));
        var result = r.ProcessIncomingPart(Part("B", "s1", 1, 1));
        Assert.NotNull(result);
        Assert.Equal("s1", result.Text);
        Assert.Equal("B", result.SenderNumber);
    }
}


// ==================== GetRecentSmsMessagesAsync 分页快照 ====================
public class SmsPagedQueryTests : IDisposable
{
    private readonly PreferenceDatabaseService _db;
    public SmsPagedQueryTests() => _db = new PreferenceDatabaseService(Path.Combine(Path.GetTempPath(), $"vowin-sms-paged-{Guid.NewGuid():N}.db"));
    public void Dispose() { }

    private static SmsMessageModel Mk(int n, string num = "+10000000000")
        => new() { Id = $"id{n:000}", SenderOrRecipient = num, Text = $"msg{n}", Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(n) };

    [Fact]
    public async Task Recent_ReturnsLastNInAscendingOrder()
    {
        for (var i = 0; i < 8; i++) await _db.SaveSmsMessageAsync(Mk(i));
        var recent = await _db.GetRecentSmsMessagesAsync(3);
        Assert.Equal(3, recent.Count);
        Assert.Equal("msg5", recent[0].Text);
        Assert.Equal("msg7", recent[^1].Text); // 升序返回，最近一条在末尾
    }

    [Fact]
    public async Task Recent_LimitExceedsCount_ReturnsAllAscending()
    {
        for (var i = 0; i < 4; i++) await _db.SaveSmsMessageAsync(Mk(i));
        var all = await _db.GetRecentSmsMessagesAsync(500);
        Assert.Equal(4, all.Count);
        Assert.Equal("msg0", all[0].Text);
        Assert.Equal("msg3", all[^1].Text);
    }

    [Fact]
    public async Task GetAll_MatchesAscendingSnapshot()
    {
        for (var i = 0; i < 5; i++) await _db.SaveSmsMessageAsync(Mk(i));
        var all = await _db.GetAllSmsMessagesAsync();
        Assert.Equal(new[] { "msg0", "msg1", "msg2", "msg3", "msg4" }, all.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task ByNumber_FiltersAndOrders()
    {
        for (var i = 0; i < 3; i++) await _db.SaveSmsMessageAsync(Mk(i));
        for (var i = 0; i < 2; i++) await _db.SaveSmsMessageAsync(Mk(i, "+20000000000"));
        var byNum = await _db.GetMessagesByNumberAsync("+20000000000");
        Assert.Equal(2, byNum.Count);
        Assert.All(byNum, m => Assert.Equal("+20000000000", m.SenderOrRecipient));
        Assert.Equal("msg0", byNum[0].Text);
    }
}


// ==================== PreferenceDatabaseService 损坏容错 ====================
public class PreferenceDbCorruptionTests
{
    [Fact]
    public async Task Initialize_SurvivesCorruptDbFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vowin-dbcorrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "prefs.db");
        File.WriteAllText(dbPath, "this is definitely not a sqlite database file");

        var db = new PreferenceDatabaseService(dbPath);
        try
        {
            await db.InitializeAsync(); // 不应抛出
            // 降级后基础读写仍可用（无论走重建还是内存模式）
            await db.SaveSmsMessageAsync(new VoWin.Models.SmsMessageModel
            {
                Id = "t1", SenderOrRecipient = "+100", Text = "hello"
            });
            var msgs = await db.GetRecentSmsMessagesAsync(10);
            Assert.Single(msgs);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Initialize_NormalDb_NotDegraded()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vowin-dbnormal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var db = new PreferenceDatabaseService(Path.Combine(dir, "prefs.db"));
        try
        {
            await db.InitializeAsync();
            Assert.False(db.IsDegraded);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}

using VoSharp.Modem.At;

namespace VoWin.Tests;

// ==================== AtResponse / AtModels ====================
public class AtModelsTests
{
    [Fact]
    public void FirstDataLine_SkipsEchoOkAndErrors()
    {
        var resp = new AtResponse(true, new[] { "AT+CSQ", "+CSQ: 20,0", "OK" });
        Assert.Equal("+CSQ: 20,0", resp.FirstDataLine);
    }

    [Fact]
    public void FirstDataLine_AllTerminal_ReturnsEmpty()
    {
        var resp = new AtResponse(true, new[] { "AT", "OK" });
        Assert.Equal(string.Empty, resp.FirstDataLine);
    }

    [Fact]
    public void FirstDataLine_SkipsCmeError()
    {
        var resp = new AtResponse(false, new[] { "+CME ERROR: 13" });
        Assert.Equal(string.Empty, resp.FirstDataLine);
    }

    [Fact]
    public void StatusDisplay_ChineseMappings()
    {
        Assert.Equal("已在网 (归属网)", new NetworkRegistration(NetworkRegStatus.Home, null, null, null).StatusDisplay);
        Assert.Equal("已在网 (漫游)", new NetworkRegistration(NetworkRegStatus.Roaming, null, null, null).StatusDisplay);
        Assert.Equal("搜索网络中...", new NetworkRegistration(NetworkRegStatus.Searching, null, null, null).StatusDisplay);
        Assert.Equal("网络拒绝注册", new NetworkRegistration(NetworkRegStatus.Denied, null, null, null).StatusDisplay);
        Assert.Equal("未注册网络", new NetworkRegistration(NetworkRegStatus.NotRegistered, null, null, null).StatusDisplay);
        Assert.Equal("未知状态", new NetworkRegistration(NetworkRegStatus.Unknown, null, null, null).StatusDisplay);
    }

    [Fact]
    public void NetworkRegistration_StateMatchesEnum()
    {
        var reg = new NetworkRegistration(NetworkRegStatus.Roaming, "China Mobile", "LTE", "ABCD");
        Assert.Equal("Roaming", reg.State);
        Assert.Equal("China Mobile", reg.OperatorName);
        Assert.Equal("LTE", reg.AccessTechnology);
        Assert.Equal("ABCD", reg.CellId);
    }
}

// ==================== UrcParser ====================
public class UrcParserTests
{
    [Theory]
    [InlineData("RING")]
    [InlineData("+CRING: VOICE")]
    [InlineData("NO CARRIER")]
    [InlineData("BUSY")]
    [InlineData("NO ANSWER")]
    [InlineData("NO DIALTONE")]
    [InlineData("+CLIP: \"+8613800138000\",145")]
    [InlineData("+CMTI: \"SM\",3")]
    [InlineData("+CDSI: \"SM\",5")]
    [InlineData("+CEREG: 2,1")]
    [InlineData("+CREG: 1")]
    [InlineData("+CGREG: 2,5")]
    [InlineData("+CSQ: 21,99")]
    [InlineData("+CPIN: READY")]
    [InlineData("+QIND: \"csq\",20,99")]
    [InlineData("RDY")]
    [InlineData("CALL READY")]
    [InlineData("SMS READY")]
    [InlineData("+CMT: 00")] // 长度部分也识别为 URC 头
    [InlineData("+CDS: 00")]
    public void IsUrc_RecognizesAllUrcLines(string line) => Assert.True(UrcParser.IsUrc(line));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OK")]
    [InlineData("ERROR")]
    [InlineData("+CME ERROR: 100")]
    [InlineData("+CSSU: 2")]
    [InlineData("random junk")]
    public void IsUrc_RejectsNonUrcLines(string line) => Assert.False(UrcParser.IsUrc(line));

    [Fact]
    public void Parse_Ring_CallEvent()
    {
        var e = UrcParser.Parse("RING")!;
        Assert.Equal("Call", e.Category);
        Assert.Equal("RING", e.ParsedData);
    }

    [Fact]
    public void Parse_Clip_ExtractsCallerNumber()
    {
        var e = UrcParser.Parse("+CLIP: \"+8613800138000\",145")!;
        Assert.Equal("CallIncoming", e.Category);
        Assert.Equal("+8613800138000", e.ParsedData);
    }

    [Fact]
    public void Parse_Clip_WithoutNpi()
    {
        var e = UrcParser.Parse("+CLIP: \"10086\"")!;
        Assert.Equal("CallIncoming", e.Category);
        Assert.Equal("10086", e.ParsedData);
    }

    [Theory]
    [InlineData("+CEREG: 2,1", "HOME")]
    [InlineData("+CEREG: 2,2", "SEARCHING")]
    [InlineData("+CEREG: 2,5", "ROAMING")]
    [InlineData("+CEREG: 2,0", "UNREGISTERED")]
    [InlineData("+CEREG: 2,3", "UNREGISTERED")] // Denied → UNREGISTERED
    [InlineData("+CREG: 1", "HOME")]
    public void Parse_Registration_StatusMapping(string line, string expected)
    {
        var e = UrcParser.Parse(line)!;
        Assert.Equal("Registration", e.Category);
        Assert.Equal(expected, e.ParsedData);
    }

    [Theory]
    [InlineData("+CSQ: 0,99", -113, 0)]
    [InlineData("+CSQ: 1,99", -111, 1)]
    [InlineData("+CSQ: 4,99", -105, 2)]
    [InlineData("+CSQ: 9,99", -95, 3)]
    [InlineData("+CSQ: 14,99", -85, 4)]
    [InlineData("+CSQ: 19,99", -75, 5)]
    [InlineData("+CSQ: 31,99", -51, 5)]
    [InlineData("+CSQ: 99,99", 0, 0)] // 99 = unknown
    public void Parse_Csq_RssiAndBars(string line, int expectedDbm, int expectedBars)
    {
        var e = UrcParser.Parse(line)!;
        Assert.Equal("Signal", e.Category);
        var t = e.ParsedData!.GetType();
        Assert.Equal(expectedDbm, (int)t.GetProperty("RSSI")!.GetValue(e.ParsedData)!);
        Assert.Equal(expectedBars, (int)t.GetProperty("Bars")!.GetValue(e.ParsedData)!);
    }

    [Fact]
    public void Parse_Cmti_StorageAndIndex()
    {
        var e = UrcParser.Parse("+CMTI: \"SM\",7")!;
        Assert.Equal("SmsNotification", e.Category);
    }

    [Fact]
    public void Parse_Cdsi_StatusReportNotification()
    {
        var e = UrcParser.Parse("+CDSI: \"ME\",2")!;
        Assert.Equal("SmsStatusReportNotification", e.Category);
    }

    [Theory]
    [InlineData("+CPIN: READY", "READY")]
    [InlineData("+CPIN: SIM PIN", "SIM PIN")]
    [InlineData("+CPIN: SIM PUK", "SIM PUK")]
    public void Parse_Cpin_Status(string line, string expected)
    {
        var e = UrcParser.Parse(line)!;
        Assert.Equal("SimStatus", e.Category);
        Assert.Equal(expected, e.ParsedData);
    }

    [Fact]
    public void Parse_Qind_FallsBackToGeneric()
    {
        var e = UrcParser.Parse("+QIND: \"sms done\"")!;
        Assert.Equal("Generic", e.Category);
        Assert.Equal("+QIND: \"sms done\"", e.ParsedData);
    }

    [Fact]
    public void Parse_NonUrc_ReturnsNull() => Assert.Null(UrcParser.Parse("+CSSU: 2"));

    [Theory]
    [InlineData("AT+CPIN?", "+CPIN: READY", true)]
    [InlineData("AT+CSQ", "+CSQ: 20,0", true)]
    [InlineData("AT+CREG?", "+CREG: 2,1", true)]
    [InlineData("AT+CEREG?", "+CEREG: 2,1", true)]
    [InlineData("AT+CGREG?", "+CGREG: 2,1", true)]
    [InlineData("AT+CSQ", "+CREG: 2,1", false)] // 响应与命令不匹配
    [InlineData("AT+CMGL=1", "+CMGL: 1,,", false)] // 未列入期望集合
    [InlineData(null, "+CSQ: 20,0", false)] // 无命令
    [InlineData("", "+CSQ: 20,0", false)]
    public void IsExpectedCommandResponse_MatchesCommandToPrefix(string? cmd, string line, bool expected)
        => Assert.Equal(expected, UrcParser.IsExpectedCommandResponse(cmd, line));
}

// ==================== AtResponseRouter ====================
public class AtResponseRouterTests
{
    private static TaskCompletionSource<AtResponse> NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Ok_CompletesPendingWithSuccess()
    {
        var r = new AtResponseRouter();
        var tcs = NewTcs();
        r.BeginCommand("AT", tcs);
        r.HandleLine("AT");
        r.HandleLine("OK");
        var resp = await tcs.Task;
        Assert.True(resp.Success);
        Assert.Null(resp.ErrorCode);
        Assert.Equal("AT\r\nOK".Replace("\r", ""), resp.RawOutput?.Replace("\r", ""));
        Assert.Equal(2, resp.Lines.Count);
    }

    [Fact]
    public async Task Error_CompletesPendingWithFailure()
    {
        var r = new AtResponseRouter();
        var tcs = NewTcs();
        r.BeginCommand("AT+BAD", tcs);
        r.HandleLine("ERROR");
        var resp = await tcs.Task;
        Assert.False(resp.Success);
        Assert.Equal("ERROR", resp.ErrorCode);
    }

    [Theory]
    [InlineData("+CME ERROR: 13")]
    [InlineData("+CMS ERROR: 500")]
    public async Task ExtendedError_CompletesPendingWithFailure(string err)
    {
        var r = new AtResponseRouter();
        var tcs = NewTcs();
        r.BeginCommand("AT+X", tcs);
        r.HandleLine(err);
        var resp = await tcs.Task;
        Assert.False(resp.Success);
        Assert.Equal(err, resp.ErrorCode);
    }

    [Fact]
    public void BeginCommand_WhilePending_Throws()
    {
        var r = new AtResponseRouter();
        r.BeginCommand("AT", NewTcs());
        Assert.Throws<InvalidOperationException>(() => r.BeginCommand("AT", NewTcs()));
    }

    [Fact]
    public void BeginCommand_LateDrainPending_Throws()
    {
        var r = new AtResponseRouter();
        var tcs = NewTcs();
        r.BeginCommand("AT", tcs);
        r.Timeout(tcs);
        // Timeout 后 pending 已完成但 lateDrain 仍在等待终结行
        Assert.Throws<InvalidOperationException>(() => r.BeginCommand("AT", NewTcs()));
    }

    [Fact]
    public async Task Timeout_CompletesWithTimeoutError()
    {
        var r = new AtResponseRouter();
        var tcs = NewTcs();
        r.BeginCommand("AT+CSQ", tcs);
        r.Timeout(tcs);
        var resp = await tcs.Task;
        Assert.False(resp.Success);
        Assert.Equal("TIMEOUT", resp.ErrorCode);
    }

    [Fact]
    public async Task Timeout_LateOk_DrainsAndCompletesDrain()
    {
        var r = new AtResponseRouter();
        var tcs = NewTcs();
        r.BeginCommand("AT+CSQ", tcs);
        r.Timeout(tcs);
        Assert.False(tcs.Task.IsCompleted == false);

        // 迟到的非终结行不结束 drain；终结行 OK 才结束
        r.HandleLine("+CSQ: 20,0");
        var drainTask = r.AwaitPreviousTerminalAsync(new CancellationTokenSource(500).Token);
        r.HandleLine("OK");
        Assert.True(await drainTask);
    }

    [Fact]
    public async Task ResetOnClose_CompletesPendingWithPortClosed()
    {
        var r = new AtResponseRouter();
        var tcs = NewTcs();
        r.BeginCommand("AT", tcs);
        r.ResetOnClose();
        var resp = await tcs.Task;
        Assert.False(resp.Success);
        Assert.Equal("PORT_CLOSED", resp.ErrorCode);
    }

    [Fact]
    public async Task UrcDuringCommand_RaisedButNotStoredAsResponse()
    {
        var r = new AtResponseRouter();
        var urcs = new List<string>();
        r.UrcReceived += urcs.Add;
        var tcs = NewTcs();
        r.BeginCommand("AT+CSQ", tcs);
        r.HandleLine("+CMTI: \"SM\",1"); // 纯 URC，不是 CSQ 期望响应
        r.HandleLine("+CSQ: 20,0");
        r.HandleLine("OK");
        var resp = await tcs.Task;
        Assert.Equal("+CSQ: 20,0", resp.FirstDataLine);
        Assert.Contains("+CMTI: \"SM\",1", urcs);
    }

    [Fact]
    public async Task ExpectedUrcLikeResponse_StoredInCommandResponse()
    {
        var r = new AtResponseRouter();
        var tcs = NewTcs();
        r.BeginCommand("AT+CSQ", tcs);
        r.HandleLine("+CSQ: 20,0"); // CSQ 期望响应，既是 URC 又是数据行
        r.HandleLine("OK");
        var resp = await tcs.Task;
        Assert.True(resp.Success);
        Assert.Contains("+CSQ: 20,0", resp.Lines);
    }

    [Fact]
    public async Task MultilineUrc_CmtHeaderAndBodyCombined()
    {
        var r = new AtResponseRouter();
        var urcs = new List<string>();
        r.UrcReceived += urcs.Add;
        var tcs = NewTcs();
        r.BeginCommand("AT", tcs);
        r.HandleLine("+CMT: 40");
        r.HandleLine("0011000D9168...");
        r.HandleLine("OK");
        await tcs.Task;
        Assert.Single(urcs);
        Assert.Contains("0011000D9168...", urcs[0]);
        Assert.StartsWith("+CMT:", urcs[0]);
    }

    [Fact]
    public void EndCommand_WrongTcs_Ignored()
    {
        var r = new AtResponseRouter();
        var tcs1 = NewTcs();
        r.BeginCommand("AT", tcs1);
        r.EndCommand(NewTcs()); // 不匹配的 TCS 应被忽略
        r.HandleLine("OK");
        Assert.True(tcs1.Task.IsCompleted);
    }

    [Fact]
    public async Task Timeout_AfterCompletion_IsNoOp()
    {
        var r = new AtResponseRouter();
        var tcs = NewTcs();
        r.BeginCommand("AT", tcs);
        r.HandleLine("OK");
        r.Timeout(tcs); // 已完成，不应覆盖
        var resp = await tcs.Task;
        Assert.True(resp.Success);
    }

    [Fact]
    public async Task AwaitPreviousTerminal_NoDrain_ReturnsTrueImmediately()
    {
        var r = new AtResponseRouter();
        Assert.True(await r.AwaitPreviousTerminalAsync(CancellationToken.None));
    }
}

using System.Diagnostics;
using System.Text;
using VoSharp.Sim;
using VoSharp.Sim.Pcsc;

Console.OutputEncoding = Encoding.UTF8;

static string Hex(byte[]? b) => b is null ? "<null>" : Convert.ToHexString(b);

static void Line(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

// 1. 列出读卡器
var reader = new PcscReader();
if (!reader.Initialize())
{
    Line("SCardEstablishContext FAILED — 智能卡服务未运行？");
    return;
}
var readers = reader.ListReaders();
Line($"读卡器数量: {readers.Length}");
foreach (var r in readers) Line($"  - '{r}'");

if (readers.Length == 0)
{
    Line("没有找到读卡器");
    return;
}

var target = readers[0];

// 2. 直接连接
if (!reader.Connect(target))
{
    Line($"SCardConnect FAILED reader='{target}'");
    return;
}
Line($"SCardConnect OK reader='{target}'");

// 3. 手动 SELECT MF -> EF.ICCID，看原始 SW
void TrySelect(string label, string fid)
{
    try
    {
        var sel = reader.TransmitApdu(new byte[] { 0x00, 0xA4, 0x00, 0x00, 0x02, 0x3F, 0x00 });
        var sw = sel is { Length: >= 2 } ? $"{sel[^2]:X2}{sel[^1]:X2}" : "??";
        Line($"  {label} SELECT MF(3F00) -> SW={sw} raw={Hex(sel)}");
    }
    catch (Exception ex) { Line($"  {label} SELECT MF EX: {ex.GetType().Name}: {ex.Message}"); }
}

void TrySelectFile(string label, string fid)
{
    try
    {
        var hi = Convert.ToByte(fid.Substring(0, 2), 16);
        var lo = Convert.ToByte(fid.Substring(2, 2), 16);
        var sel = reader.TransmitApdu(new byte[] { 0x00, 0xA4, 0x00, 0x00, 0x02, hi, lo });
        var sw = sel is { Length: >= 2 } ? $"{sel[^2]:X2}{sel[^1]:X2}" : "??";
        Line($"  {label} SELECT {fid} -> SW={sw} raw={Hex(sel)}");
    }
    catch (Exception ex) { Line($"  {label} SELECT {fid} EX: {ex.GetType().Name}: {ex.Message}"); }
}

Line("== 手动 APDU 探测 ==");
TrySelect("manual", "3F00");
TrySelectFile("manual", "2FE2");

// 4. 尝试 READ BINARY EF.ICCID
try
{
    var read = reader.TransmitApdu(new byte[] { 0x00, 0xB0, 0x00, 0x00, 0x0A });
    var sw = read is { Length: >= 2 } ? $"{read[^2]:X2}{read[^1]:X2}" : "??";
    Line($"  READ EF.ICCID(10) -> SW={sw} raw={Hex(read)}");
}
catch (Exception ex) { Line($"  READ EF.ICCID EX: {ex.GetType().Name}: {ex.Message}"); }

// 5. 通过 PcscAkaProvider 走完整链路
Line("== PcscAkaProvider 完整链路 ==");
try
{
    using var provider = new PcscAkaProvider(target);
    Line("  provider 构造 OK");

    try
    {
        var iccid = provider.ReadIccidAsync().GetAwaiter().GetResult();
        Line($"  ReadIccidAsync OK tail=...{iccid[^4..]} len={iccid.Length}");
    }
    catch (Exception ex) { Line($"  ReadIccidAsync FAIL: {ex.GetType().Name}: {ex.Message}"); }

    try
    {
        var imsi = provider.ReadImsiAsync().GetAwaiter().GetResult();
        Line($"  ReadImsiAsync OK tail=...{imsi[^3..]} len={imsi.Length}");
    }
    catch (Exception ex) { Line($"  ReadImsiAsync FAIL: {ex.GetType().Name}: {ex.Message}"); }
}
catch (Exception ex)
{
    Line($"  provider 构造 FAIL: {ex.GetType().Name}: {ex.Message}");
}

// 6. 断电复位后再试
Line("== SCARD_UNPOWER_CARD 断电复位后重试 ==");
try
{
    reader.Disconnect(WinSCard.SCARD_UNPOWER_CARD);
    Line("  已发送 UNPOWER_CARD");
}
catch (Exception ex) { Line($"  UNPOWER 异常: {ex.Message}"); }

Thread.Sleep(3000);

try
{
    if (!reader.Connect(target)) { Line("  复位后 Connect 仍失败"); }
    else
    {
        Line("  复位后 Connect OK");
        TrySelect("after-reset", "3F00");
        TrySelectFile("after-reset", "2FE2");
        try
        {
            var read = reader.TransmitApdu(new byte[] { 0x00, 0xB0, 0x00, 0x00, 0x0A });
            var sw = read is { Length: >= 2 } ? $"{read[^2]:X2}{read[^1]:X2}" : "??";
            Line($"  复位后 READ EF.ICCID -> SW={sw} raw={Hex(read)}");
        }
        catch (Exception ex) { Line($"  复位后 READ EX: {ex.Message}"); }
    }
}
catch (Exception ex) { Line($"  复位后重试异常: {ex.Message}"); }

reader.Dispose();
Line("完成");

// ── 场景 A：模拟“另一个进程/连接持有卡片独占权” → 验证 attach 的断电复位恢复能力 ──
// 场景 B：模拟“卡片被断电后重新初始化” → 验证 PowerCycleAndReconnect 可用
Line("");
Line("== 验证 PcscReader.PowerCycleAndReconnect ==");
var r2 = new PcscReader();
if (r2.Initialize())
{
    var r2Name = r2.ListReaders().FirstOrDefault();
    if (!string.IsNullOrEmpty(r2Name))
    {
        if (!r2.Connect(r2Name)) Line("  首次 Connect 失败");
        else
        {
            Line("  首次 Connect OK");
            var ok = r2.PowerCycleAndReconnect(r2Name);
            Line($"  PowerCycleAndReconnect => {ok}");
            if (ok)
            {
                var sel = r2.TransmitApdu(new byte[] { 0x00, 0xA4, 0x00, 0x00, 0x02, 0x3F, 0x00 });
                var sw = sel is { Length: >= 2 } ? $"{sel[^2]:X2}{sel[^1]:X2}" : "??";
                Line($"  复位后 SELECT MF -> SW={sw}");
            }
        }
    }
    r2.Dispose();
}
Line("验证完成");

// ══════════════════════════════════════════════════════════════════
// 场景 B：模拟真实故障——另一个上下文以 SHARED 方式连上卡片后，
// 卡片进入 SIM REFRESH 锁定态（select 返回 6985），验证
// AttachPcscAsync 的断电复位自愈路径能否恢复。
// ══════════════════════════════════════════════════════════════════
Line("");
Line("== 场景 B：卡片锁定 → AttachPcscAsync 自愈验证 ==");

var readerName = new PcscReader();
readerName.Initialize();
var rn = readerName.ListReaders().FirstOrDefault();
readerName.Dispose();

if (string.IsNullOrEmpty(rn)) { Line("  无读卡器，跳过"); return; }

// 步骤 1：用一个独立连接“占用”卡片，并持续 SELECT制造锁定
var holder = new PcscReader();
holder.Initialize();
holder.Connect(rn);
Line("  占用连接已建立");

// 步骤 2：模拟读卡失败——此处用 provider 读取，观察是否成功
// （正常情况下应当成功，因为 SHARED 模式允许共享）
try
{
    using var p = new PcscAkaProvider(rn);
    var iccid = p.ReadIccidAsync().GetAwaiter().GetResult();
    Line($"  占用状态下读取成功 tail=...{iccid[^4..]}（SHARED 模式共享正常）");
}
catch (Exception ex)
{
    Line($"  占用状态下读取失败: {ex.GetType().Name}: {ex.Message}");
    Line("  → 触发自愈路径验证");

    // 自愈：PowerCycleCard
    try
    {
        using var p2 = new PcscAkaProvider(rn);
        var ok = p2.PowerCycleCard();
        Line($"  PowerCycleCard => {ok}");
        var ic2 = p2.ReadIccidAsync().GetAwaiter().GetResult();
        Line($"  自愈后读取成功 tail=...{ic2[^4..]}");
    }
    catch (Exception ex2)
    {
        Line($"  自愈失败: {ex2.GetType().Name}: {ex2.Message}");
    }
}

holder.Dispose();
Line("场景 B 完成");

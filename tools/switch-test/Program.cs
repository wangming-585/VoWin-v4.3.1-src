using System.Text;
using VoSharp.Common.Events;
using VoSharp.Kernel.Pool;

Console.OutputEncoding = Encoding.UTF8;
static void Line(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

// 只在显式带 --yes 时才真正切卡，避免误触发
var confirmed = args.Contains("--yes");

var bus = new AsyncEventBus();
bus.Subscribe(EventTopics.SystemLog, evt =>
{
    var text = evt.Payload?.ToString() ?? "";
    if (text.Contains("eSIM") || text.Contains("PC/SC") || text.Contains("断电复位") || text.Contains("重连"))
        Line($"  [log] {text}");
});

var pool = new ModemPool(bus);

Line("=== 1) 发现卡槽 ===");
var slots = await pool.DiscoverPcscSlotsAsync(CancellationToken.None);
if (slots.Count == 0) { Line("无卡槽，退出"); return; }
var slot = slots[0];
Line($"slot={slot.Id} state={slot.State} activeIccidTail=...{Tail(slot.Sim?.Iccid)}");

Line("");
Line("=== 2) 读取 eSIM profile 列表 ===");
var profiles = await slot.GetEuiccProfilesAsync(CancellationToken.None);
Line($"共 {profiles.Count} 个 profile");
foreach (var p in profiles)
    Line($"  {(p.IsEnabledState ? "[Enabled ]" : "[Disabled]")} ICCID=...{Tail(p.ICCID)}  {p.Nickname ?? p.ProfileName ?? "-"}");

var active = profiles.FirstOrDefault(p => p.IsEnabledState);
Line($"当前启用: {(active == null ? "<无>" : "..." + Tail(active.ICCID))}");

var target = profiles.FirstOrDefault(p => !p.IsEnabledState && p.ProfileClass == VoSharp.Euicc.Models.ProfileClass.Operational);
if (target == null) { Line("没有可切换的目标 profile，退出"); return; }
Line($"切换目标: ...{Tail(target.ICCID)}  {target.Nickname ?? target.ProfileName ?? "-"}");

if (!confirmed)
{
    Line("");
    Line("!! 未带 --yes，仅做只读检查，不执行切卡。");
    return;
}

Line("");
Line("=== 3) 执行切卡（真实操作） ===");
var sw = System.Diagnostics.Stopwatch.StartNew();
var progress = new Progress<VoSharp.Euicc.Models.ProfileSwitchProgress>(p =>
    Line($"  [{p.Step}/{p.TotalSteps}] {p.Percent}% {p.Stage}{(p.Failed ? "  **FAILED**" : "")} {p.Detail}"));
bool ok = false;
try
{
    ok = await slot.SwitchEuiccProfileAsync(target.ICCID, refresh: true, CancellationToken.None, progress: progress);
}
catch (Exception ex)
{
    Line($"切卡异常: {ex.GetType().Name}: {ex.Message}");
}
sw.Stop();
Line($"切卡结果: {(ok ? "成功" : "失败")}，耗时 {sw.ElapsedMilliseconds} ms");
Line($"切换后 slot.Sim 尾号 = ...{Tail(slot.Sim?.Iccid)}（期望 ...{Tail(target.ICCID)}）");
Line($"切换后 state={slot.State}");
Line($"切换后 eUICC 能力={slot.EuiccCapability}");

Line("");
Line("=== 4) 切回原 profile（恢复现场） ===");
if (active != null && ok)
{
    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var back = await slot.SwitchEuiccProfileAsync(active.ICCID, refresh: true, CancellationToken.None);
        sw2.Stop();
        Line($"切回结果: {(back ? "成功" : "失败")}，耗时 {sw2.ElapsedMilliseconds} ms，当前尾号 ...{Tail(slot.Sim?.Iccid)}");
    }
    catch (Exception ex) { Line($"切回异常: {ex.Message}"); }
}
else
{
    Line("未执行切回（原 profile 未知或首次切卡失败）");
}

Line("测试完成");

static string Tail(string? s) => string.IsNullOrEmpty(s) || s.Length < 4 ? "<none>" : s[^4..];

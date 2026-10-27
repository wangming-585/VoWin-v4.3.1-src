using System.Text;
using VoSharp.Common.Events;
using VoSharp.Kernel.Pool;

Console.OutputEncoding = Encoding.UTF8;

static void Line(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

var bus = new AsyncEventBus();
int errorEvents = 0;
bus.Subscribe("system.log", evt =>
{
    // AsyncEventBus 的负载类型是字符串；这里只做计数与打印，便于看清自愈过程
    var text = evt?.Payload?.ToString() ?? "";
    if (text.Contains("FAILED") || text.Contains("失败") || text.Contains("复位")) errorEvents++;
    return Task.CompletedTask;
});

var pool = new ModemPool(bus);

Line("=== 1) 枚举读卡器 ===");
var readers = ModemPool.ListPcscReaders();
foreach (var r in readers) Line($"  reader: '{r}'");
if (readers.Count == 0) { Line("没有读卡器，结束"); return; }

Line("");
Line("=== 2) 走生产路径 DiscoverPcscSlotsAsync（L2 看门狗同款） ===");
var sw = System.Diagnostics.Stopwatch.StartNew();
var discovered = await pool.DiscoverPcscSlotsAsync(CancellationToken.None);
sw.Stop();
Line($"  发现卡槽数: {discovered.Count}，耗时 {sw.ElapsedMilliseconds} ms");

foreach (var s in discovered)
{
    Line($"  slot {s.Id}");
    Line($"    State      = {s.State}");
    Line($"    Reader     = {s.PcscReaderName}");
    Line($"    ICCID tail = ...{(s.Sim?.Iccid is { Length: >= 4 } ic ? ic[^4..] : "<none>")}");
    Line($"    IMSI len   = {s.Sim?.Imsi?.Length ?? 0}");
    Line($"    LastError  = {s.LastError ?? "<none>"}");
}

if (discovered.Count == 0)
{
    Line("  !! 未附着任何卡槽，测试失败");
    foreach (var s in pool.Slots.Values)
        Line($"  残留 slot {s.Id}: state={s.State} err={s.LastError}");
    return;
}

Line("");
Line("=== 3) 重复附着（验证幂等 / 模拟 L2 周期重连） ===");
var again = await pool.AddOrUpdatePcscSlotAsync(readers[0], CancellationToken.None);
Line($"  再次附着 => {(again != null ? "成功" : "失败")}, state={again?.State}, iccidTail=...{(again?.Sim?.Iccid is { Length: >= 4 } i2 ? i2[^4..] : "<none>")}");

Line("");
Line("=== 4) 连续 3 轮附着压力（模拟看门狗反复巡检） ===");
for (var i = 1; i <= 3; i++)
{
    var t = System.Diagnostics.Stopwatch.StartNew();
    var ok = await pool.AddOrUpdatePcscSlotAsync(readers[0], CancellationToken.None);
    t.Stop();
    Line($"  第 {i} 轮: {(ok != null ? "OK" : "FAIL")} {t.ElapsedMilliseconds} ms, state={ok?.State}");
}

Line("");
Line($"=== 结果：自愈/错误相关事件计数 = {errorEvents} ===");
Line("测试完成");

Line("");
Line("=== 5) 自愈路径验证：断电复位后重新读卡（AttachPcscAsync 的 catch 分支同款） ===");
foreach (var slot in pool.Slots.Values.Where(s => s.IsPcscReader))
{
    if (slot.Aka is VoSharp.Sim.PcscAkaProvider prov)
    {
        try
        {
            var cycled = prov.PowerCycleCard();
            Line($"  PowerCycleCard => {cycled}");
            if (cycled)
            {
                var id2 = await prov.ReadIdentityAsync(CancellationToken.None);
                Line($"  复位后读取成功: ICCID tail=...{id2.Iccid[^4..]} IMSI len={id2.Imsi.Length}");
            }
        }
        catch (Exception ex)
        {
            Line($"  自愈路径异常: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

Line("");
Line("=== 6) 连续 3 次断电复位压力（验证复位不会恶化卡片状态） ===");
foreach (var slot in pool.Slots.Values.Where(s => s.IsPcscReader))
{
    if (slot.Aka is VoSharp.Sim.PcscAkaProvider prov2)
    {
        for (var i = 1; i <= 3; i++)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            var ok = prov2.PowerCycleCard();
            string tail = "<null>";
            if (ok)
            {
                var id3 = await prov2.ReadIdentityAsync(CancellationToken.None);
                if (id3.Iccid.Length >= 4) tail = id3.Iccid[^4..];
            }
            t.Stop();
            Line($"  第 {i} 次: cycle={ok}, iccidTail=...{tail}, {t.ElapsedMilliseconds} ms");
        }
    }
}
Line("自愈验证完成");

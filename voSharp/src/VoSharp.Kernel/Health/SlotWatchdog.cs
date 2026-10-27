using VoSharp.Common.Events;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.VoWifi;

namespace VoSharp.Kernel.Health;

/// <summary>
/// L1 watchdog: walks every slot on a 5 s cadence, refreshes telemetry via the
/// existing <see cref="ModemSlot.RefreshMetricsAsync"/>, keeps the UI's data
/// fresh, detects anomaly states and performs lightweight self-repair
/// (PC/SC re-attach). Persistent faults are escalated to the L2 pool watchdog;
/// a sliding-window budget prevents repair storms on physically absent devices.
/// </summary>
public sealed class SlotWatchdog : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    /// <summary>Data older than this is reported as stale (six missed refresh cycles).</summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(30);

    private readonly ModemPool _pool;
    private readonly WatchdogHealthRegistry _registry;
    private readonly AsyncEventBus? _eventBus;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _running;

    public SlotWatchdog(
        ModemPool pool,
        WatchdogHealthRegistry registry,
        AsyncEventBus? eventBus = null,
        TimeSpan? interval = null)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _eventBus = eventBus;
        _interval = interval ?? DefaultInterval;
    }

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        _loop = Task.Run(() => RunAsync(_cts.Token));
        _eventBus?.Publish(EventTopics.SystemLog, "SlotWatchdog",
            $"L1 卡槽看门狗已启动，巡检周期 {_interval.TotalSeconds:0}s。");
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        try { _cts.Cancel(); } catch { }
        if (_loop != null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProbeAllAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _eventBus?.Publish(EventTopics.SystemLog, "SlotWatchdog",
                    $"L1 巡检异常（本轮跳过）: {ex.Message}");
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProbeAllAsync(CancellationToken ct)
    {
        foreach (var slot in _pool.Slots.Values.ToList())
        {
            if (ct.IsCancellationRequested) return;

            // A profile switch owns the AT/SIM channel; probing now would race
            // the switch sequence (RefreshMetricsAsync also guards internally).
            if (slot.IsProfileSwitchInProgress)
            {
                _registry.Publish(slot.Id, HealthVerdict.Stale, DateTime.UtcNow - slot.LastSeen,
                    "eSIM 切卡进行中，本轮跳过巡检。");
                continue;
            }

            // Never inject AT traffic during an active call: baseband collision
            // would degrade the voice path. The existing RefreshMetricsAsync
            // guard covers its own paths; this layer must skip as well.
            if (slot.Calls.ActiveCall != null)
                continue;

            var ageBefore = DateTime.UtcNow - slot.LastSeen;
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(15));
                await slot.RefreshMetricsAsync(probeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-slot probe timeout counts as a failed cycle below.
            }

            var verdict = Evaluate(slot, out var detail);
            var age = DateTime.UtcNow - slot.LastSeen;

            // Lightweight self-repair: re-attach a PC/SC reader whose ADF.USIM
            // stopped responding. Serial-modem hardware reattach belongs to L2.
            //
            // 关键约束：只有 Failed（读卡器/卡片真的不可用）才值得重新附着。
            // Degraded 的典型来源是 VoWiFi 注册失败（例如 ePDG DNS 解析不出地址），
            // 那是网络侧问题，重新附着读卡器完全无效，反而每 5 秒把读卡器
            // detach/attach 一次 —— 制造出探测必然失败的窗口，进而把 eSIM
            // 列表刷空。历史上这里累计空转了 296 次，就是这么来的。
            if (verdict == HealthVerdict.Failed && slot.IsPcscReader && !string.IsNullOrEmpty(slot.PcscReaderName))
            {
                // 本轮巡检开始后才启动的切卡也要让路：刷新指标期间用户可能已点了
                // 切卡，此刻重新附着（尤其是断电复位）会直接打断切卡流程。
                if (slot.IsProfileSwitchInProgress)
                {
                    _registry.Publish(slot.Id, HealthVerdict.Stale, DateTime.UtcNow - slot.LastSeen,
                        "eSIM 切卡进行中，本轮跳过重附着。");
                    continue;
                }

                if (_registry.TryConsumeRepair(slot.Id, WatchdogTier.Slot))
                {
                    _registry.RecordRecovery(slot.Id, "L1 轻量自修：重新附着 PC/SC 读卡器。");
                    try
                    {
                        if (await slot.AttachPcscAsync(slot.PcscReaderName, ct).ConfigureAwait(false))
                            verdict = HealthVerdict.Healthy;
                    }
                    catch (OperationCanceledException) { }
                    catch { /* failure keeps the degraded verdict */ }
                }
                else
                {
                    detail = "L1 修复预算已耗尽，熔断暂停干预。";
                }
            }

            if (verdict == HealthVerdict.Healthy)
            {
                _registry.Publish(slot.Id, HealthVerdict.Healthy, age, detail);
                if (_registry.Get(slot.Id)?.Escalation == EscalationLevel.ToPool)
                    _registry.SetEscalation(slot.Id, EscalationLevel.None);
                continue;
            }

            // Publish() already counted this cycle's failure, so the counter
            // here includes the current unhealthy pass.
            var failures = _registry.Get(slot.Id)?.ConsecutiveFailures ?? 0;
            var overThreshold = failures >= WatchdogHealthRegistry.PoolEscalationThreshold;
            var budgetGone = _registry.IsBudgetExhausted(slot.Id, WatchdogTier.Slot);

            if (budgetGone || overThreshold)
                _registry.SetEscalation(slot.Id, EscalationLevel.ToPool);

            _registry.Publish(slot.Id, verdict, age,
                budgetGone ? "L1 修复预算耗尽，升级 L2 并熔断。" : detail);
        }
    }

    private static HealthVerdict Evaluate(ModemSlot slot, out string? detail)
    {
        detail = null;

        if (slot.State == SlotState.Error)
        {
            detail = slot.LastError ?? "卡槽处于错误状态。";
            return HealthVerdict.Failed;
        }

        if (slot.State == SlotState.Offline)
        {
            detail = "设备离线（串口或读卡器不可用）。";
            return HealthVerdict.Failed;
        }

        var age = DateTime.UtcNow - slot.LastSeen;
        if (age > StaleThreshold)
        {
            detail = $"指标数据已 {age.TotalSeconds:0}s 未刷新。";
            return HealthVerdict.Stale;
        }

        if (slot.VoWifi.KeepOnlineRequested && slot.VoWifi.State == VoWifiState.Failed)
        {
            detail = "VoWiFi 期望在线但当前处于失败状态。";
            return HealthVerdict.Degraded;
        }

        if (!slot.IsPcscReader && slot.Sim == null && slot.Modem is { IsOpen: true })
        {
            detail = "SIM 意外消失（模组在线但无 SIM 身份）。";
            return HealthVerdict.Degraded;
        }

        return HealthVerdict.Healthy;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

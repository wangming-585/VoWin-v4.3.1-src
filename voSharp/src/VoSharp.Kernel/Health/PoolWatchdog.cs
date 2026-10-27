using VoSharp.Common.Events;
using VoSharp.Kernel.Pool;

namespace VoSharp.Kernel.Health;

/// <summary>
/// L2 watchdog: on a 15 s cadence reconciles the pool against the actual
/// hardware — enumerates PC/SC readers, detects readers that appeared or
/// disappeared, re-attaches slots escalated by L1 using the pool's existing
/// 5-attempt backoff, and reopens serial ports for long-offline AT modems.
/// Failures beyond the pool budget are escalated to the L3 global watchdog.
/// </summary>
public sealed class PoolWatchdog : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    private readonly ModemPool _pool;
    private readonly WatchdogHealthRegistry _registry;
    private readonly AsyncEventBus? _eventBus;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _running;
    private int _reconciling;

    public PoolWatchdog(
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
        _eventBus?.Publish(EventTopics.SystemLog, "PoolWatchdog",
            $"L2 设备池看门狗已启动，巡检周期 {_interval.TotalSeconds:0}s。");
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
                await ReconcileAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _eventBus?.Publish(EventTopics.SystemLog, "PoolWatchdog",
                    $"L2 巡检异常（本轮跳过）: {ex.Message}");
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        // Never run two reconciliations concurrently; also gives any in-flight
        // AddOrUpdate/Remove cycle in the pool a chance to settle.
        if (Interlocked.Exchange(ref _reconciling, 1) != 0) return;
        try
        {
            var readers = ModemPool.ListPcscReaders();
            var readerSet = new HashSet<string>(readers, StringComparer.OrdinalIgnoreCase);

            await ReconcilePcscSlotsAsync(readerSet, ct).ConfigureAwait(false);
            await ReconcileSerialSlotsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _reconciling, 0);
        }
    }

    private async Task ReconcilePcscSlotsAsync(HashSet<string> readerSet, CancellationToken ct)
    {
        var pcscSlots = _pool.Slots.Values.Where(s => s.IsPcscReader).ToList();

        // 1. Escalated or non-online slots whose reader is still present:
        //    reuse the pool's 5-attempt backoff reattach instead of a new loop.
        foreach (var slot in pcscSlots)
        {
            if (ct.IsCancellationRequested) return;
            if (slot.IsProfileSwitchInProgress) continue;

            var readerPresent = slot.PcscReaderName != null && readerSet.Contains(slot.PcscReaderName);
            var escalated = _registry.Get(slot.Id)?.Escalation == EscalationLevel.ToPool;

            if (readerPresent && slot.State != SlotState.Online)
            {
                if (!_registry.TryConsumeRepair(slot.Id, WatchdogTier.Pool)) continue;
                _registry.RecordRecovery(slot.Id, $"L2 重连读卡器 '{slot.PcscReaderName}'（5 次退避重试）。");
                var reattached = await _pool.AddOrUpdatePcscSlotAsync(slot.PcscReaderName!, ct).ConfigureAwait(false);
                if (reattached != null)
                {
                    _registry.Publish(slot.Id, HealthVerdict.Healthy, TimeSpan.Zero, "L2 重连读卡器成功。");
                    _registry.SetEscalation(slot.Id, EscalationLevel.None);
                }
                else if (_registry.IsBudgetExhausted(slot.Id, WatchdogTier.Pool))
                {
                    _registry.SetEscalation(slot.Id, EscalationLevel.ToGlobal);
                    _registry.Publish(slot.Id, HealthVerdict.Failed, DateTime.UtcNow - slot.LastSeen,
                        "L2 重连失败且预算耗尽，升级 L3 并熔断。");
                }
            }
            else if (!readerPresent && escalated)
            {
                // Reader physically absent: nothing to attach to. Mark failed;
                // when the device is plugged back in it will be reconciled as a
                // present reader above (or a new slot below).
                _registry.Publish(slot.Id, HealthVerdict.Failed, DateTime.UtcNow - slot.LastSeen,
                    $"读卡器 '{slot.PcscReaderName}' 不在系统中。");
            }
        }

        // 2. Readers that appeared without a slot: create them through the
        //    same pool path used by manual discovery.
        foreach (var readerName in readerSet)
        {
            if (ct.IsCancellationRequested) return;
            var known = pcscSlots.Any(s =>
                string.Equals(s.PcscReaderName, readerName, StringComparison.OrdinalIgnoreCase));
            if (known) continue;

            _eventBus?.Publish(EventTopics.SystemLog, "PoolWatchdog",
                $"L2 发现新读卡器 '{readerName}'，正在创建卡槽…");
            var slot = await _pool.AddOrUpdatePcscSlotAsync(readerName, ct).ConfigureAwait(false);
            if (slot != null)
            {
                _registry.Publish(slot.Id, HealthVerdict.Healthy, TimeSpan.Zero, "L2 创建 PC/SC 卡槽成功。");
            }
        }
    }

    private async Task ReconcileSerialSlotsAsync(CancellationToken ct)
    {
        foreach (var slot in _pool.Slots.Values.Where(s => !s.IsPcscReader).ToList())
        {
            if (ct.IsCancellationRequested) return;
            if (slot.IsProfileSwitchInProgress) continue;
            if (slot.State != SlotState.Offline) continue;

            var escalated = _registry.Get(slot.Id)?.Escalation == EscalationLevel.ToPool;
            var failures = _registry.Get(slot.Id)?.ConsecutiveFailures ?? 0;
            if (!escalated && failures < WatchdogHealthRegistry.PoolEscalationThreshold) continue;
            if (!_registry.TryConsumeRepair(slot.Id, WatchdogTier.Pool)) continue;

            _registry.RecordRecovery(slot.Id, $"L2 重开串口 {slot.PortName}（{slot.BaudRate} bps）。");
            try
            {
                var reattached = await _pool.AddOrUpdateSlotAsync(
                    slot.PortName,
                    slot.BaudRate,
                    name: slot.Name,
                    slotId: slot.Id,
                    proxyUrl: slot.ProxyUrl,
                    ct).ConfigureAwait(false);

                if (reattached.State == SlotState.Online)
                {
                    _registry.Publish(slot.Id, HealthVerdict.Healthy, TimeSpan.Zero, "L2 重开串口成功。");
                    _registry.SetEscalation(slot.Id, EscalationLevel.None);
                }
                else if (_registry.IsBudgetExhausted(slot.Id, WatchdogTier.Pool))
                {
                    _registry.SetEscalation(slot.Id, EscalationLevel.ToGlobal);
                    _registry.Publish(slot.Id, HealthVerdict.Failed, DateTime.UtcNow - slot.LastSeen,
                        "L2 重开串口失败且预算耗尽，升级 L3 并熔断。");
                }
            }
            catch (Exception ex)
            {
                if (_registry.IsBudgetExhausted(slot.Id, WatchdogTier.Pool))
                {
                    _registry.SetEscalation(slot.Id, EscalationLevel.ToGlobal);
                    _registry.Publish(slot.Id, HealthVerdict.Failed, DateTime.UtcNow - slot.LastSeen,
                        $"L2 重开串口异常且预算耗尽: {ex.Message}");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

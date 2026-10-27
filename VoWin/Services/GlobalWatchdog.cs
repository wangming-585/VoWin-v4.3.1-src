using System.Collections.Concurrent;
using VoSharp.Common.Events;
using VoSharp.Kernel;
using VoSharp.Kernel.Health;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.VoWifi;

namespace VoWin.Services;

/// <summary>
/// L3 global watchdog: aggregates the per-slot health view produced by L1/L2,
/// resolves their escalations, unconditionally pulls up VoWiFi whenever a slot
/// reports a ready SIM, and owns the final circuit breaker. It lives in the app
/// layer because restart decisions go through <see cref="VoKernelService"/>'s
/// VoWiFi intent mechanism (preference DB + intent versioning).
///
/// While running it takes ownership of every slot's VoWiFi recovery decision:
/// VoWifiManager's internal automatic loop then only reports via
/// <see cref="VoWifiManager.RecoveryRequested"/> instead of reconnecting on its
/// own, which prevents two recovery loops from fighting over one slot. On
/// shutdown ownership is released so the internal loop becomes the fallback.
/// </summary>
public sealed class GlobalWatchdog : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly VoKernelService _service;
    private readonly IVoKernel _kernel;
    private readonly WatchdogHealthRegistry _registry;
    private readonly AsyncEventBus? _eventBus;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, byte> _voWifiRecoveryInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _lastSuppressedLogged = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;
    private int _running;

    public GlobalWatchdog(VoKernelService service, WatchdogHealthRegistry registry, TimeSpan? interval = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _kernel = service.Kernel;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _eventBus = _kernel.EventBus;
        _interval = interval ?? DefaultInterval;
    }

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;

        foreach (var slot in _kernel.Pool.Slots.Values)
            AttachToSlot(slot);
        _kernel.Pool.SlotAdded += OnSlotAdded;
        _kernel.Pool.SlotRemoved += OnSlotRemoved;

        _loop = Task.Run(() => RunAsync(_cts.Token));
        _eventBus?.Publish(EventTopics.SystemLog, "GlobalWatchdog",
            $"L3 全局看门狗已启动，巡检周期 {_interval.TotalSeconds:0}s；已接管 VoWiFi 恢复决策权（SIM 就绪即自动拉起）。");
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;

        _kernel.Pool.SlotAdded -= OnSlotAdded;
        _kernel.Pool.SlotRemoved -= OnSlotRemoved;
        foreach (var slot in _kernel.Pool.Slots.Values)
            DetachFromSlot(slot);

        try { _cts.Cancel(); } catch { }
        if (_loop != null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        }

        // Release ownership so VoWiFi's internal automatic recovery loop
        // becomes the fallback whenever the watchdog is not running.
        foreach (var slot in _kernel.Pool.Slots.Values)
        {
            try { slot.VoWifi.SetExternalRecoveryOwner(false); } catch { }
        }
    }

    private void OnSlotAdded(object? sender, ModemSlot slot) => AttachToSlot(slot);

    private void OnSlotRemoved(object? sender, string slotId)
    {
        _voWifiRecoveryInFlight.TryRemove(slotId, out _);
        _lastSuppressedLogged.TryRemove(slotId, out _);
        _registry.Forget(slotId);
    }

    private void AttachToSlot(ModemSlot slot)
    {
        slot.VoWifi.SetExternalRecoveryOwner(true);
        slot.VoWifi.RecoveryRequested += OnVoWifiRecoveryRequested;
    }

    private void DetachFromSlot(ModemSlot slot)
    {
        slot.VoWifi.RecoveryRequested -= OnVoWifiRecoveryRequested;
    }

    private void OnVoWifiRecoveryRequested(object? sender, VoWifiManager.VoWifiRecoveryRequestedEventArgs e)
    {
        // VoWiFi internal triggers only report once L3 owns decisions. React
        // immediately instead of waiting for the next 30 s sweep, but share the
        // same in-flight gate and repair budget as the sweep.
        if (sender is not VoWifiManager voWifi) return;
        var slot = _kernel.Pool.Slots.Values.FirstOrDefault(s => ReferenceEquals(s.VoWifi, voWifi));
        if (slot == null) return;

        _ = Task.Run(() => RestartVoWifiAsync(slot, $"内部恢复上报: {e.Reason}"));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _eventBus?.Publish(EventTopics.SystemLog, "GlobalWatchdog",
                    $"L3 巡检异常（本轮跳过）: {ex.Message}");
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var slots = _kernel.Pool.Slots.Values.ToList();

        // 1. Aggregate health view (single snapshot for this sweep).
        var snapshot = _registry.Snapshot();
        var failed = snapshot.Count(h => h.Verdict is HealthVerdict.Failed or HealthVerdict.Degraded);
        if (failed > 0)
        {
            _eventBus?.Publish(EventTopics.SystemLog, "GlobalWatchdog",
                $"L3 健康视图: {slots.Count} 卡槽，{failed} 个降级/故障。");
        }

        // 2. Handle global escalations from L1/L2.
        foreach (var health in snapshot)
        {
            if (health.Escalation != EscalationLevel.ToGlobal) continue;
            var slot = slots.FirstOrDefault(s => s.Id.Equals(health.SlotId, StringComparison.OrdinalIgnoreCase));
            if (slot == null) continue;

            if (slot.Sim != null && slot.State == SlotState.Online)
            {
                // Hardware is fine; the surviving problem is the VoWiFi session.
                await RestartVoWifiAsync(slot, "L2 升级: 硬件已恢复但 VoWiFi 未注册。").ConfigureAwait(false);
            }
            else
            {
                // Hardware-level failure beyond L2's budget: one full rescan can
                // re-enumerate a re-attached reader or re-appeared COM port.
                await RescanPoolAsync(slot).ConfigureAwait(false);
            }
        }

        // 3. Unconditional VoWiFi pull-up: any slot with a ready SIM gets a
        //    VoWiFi session without consulting the DefaultVoWifi preference.
        //    An explicit in-session user Stop is still respected.
        foreach (var slot in slots)
        {
            if (ct.IsCancellationRequested) return;
            if (slot.IsProfileSwitchInProgress) continue;
            if (slot.Sim == null || slot.State != SlotState.Online) continue;

            var state = slot.VoWifi.State;
            var sessionActiveOrConnecting = state is not (VoWifiState.Disconnected or VoWifiState.Failed);
            var internalLoopAlreadyDriving = slot.VoWifi.KeepOnlineRequested && state == VoWifiState.Failed;
            if (sessionActiveOrConnecting || internalLoopAlreadyDriving) continue;

            if (_service.IsVoWifiExplicitlyDisabled(slot))
            {
                _eventBus?.Publish(EventTopics.SystemLog, "GlobalWatchdog",
                    $"卡槽 [{slot.Name}] SIM 就绪，但用户已手动关闭 VoWiFi，跳过自动拉起。");
                continue;
            }

            await RestartVoWifiAsync(slot, "L3 自动拉起: SIM 就绪。").ConfigureAwait(false);
        }
    }

    private async Task RestartVoWifiAsync(ModemSlot slot, string reason)
    {
        if (!_voWifiRecoveryInFlight.TryAdd(slot.Id, 0)) return;
        try
        {
            if (!_registry.TryConsumeRepair(slot.Id, WatchdogTier.Global))
            {
                if (_lastSuppressedLogged.TryAdd(slot.Id, true))
                {
                    _eventBus?.Publish(EventTopics.SystemLog, "GlobalWatchdog",
                        $"卡槽 [{slot.Name}] L3 修复预算耗尽，熔断暂停 VoWiFi 恢复（窗口滑过后自动恢复）。");
                    _registry.Publish(slot.Id, HealthVerdict.Suppressed, DateTime.UtcNow - slot.LastSeen,
                        "L3 熔断: VoWiFi 恢复预算耗尽。");
                }
                return;
            }

            _registry.RecordRecovery(slot.Id, $"L3 发起 VoWiFi 恢复（{reason}）");
            var started = await _service.StartVoWifiAsync(slot.Id).ConfigureAwait(false);
            _lastSuppressedLogged.TryRemove(slot.Id, out _);

            if (started)
            {
                _registry.Publish(slot.Id, HealthVerdict.Healthy, TimeSpan.Zero, "L3 VoWiFi 恢复已发起。");
                _registry.SetEscalation(slot.Id, EscalationLevel.None);
            }
            else
            {
                _eventBus?.Publish(EventTopics.SystemLog, "GlobalWatchdog",
                    $"卡槽 [{slot.Name}] L3 VoWiFi 恢复启动失败，将按预算退避重试。");
                if (_registry.IsBudgetExhausted(slot.Id, WatchdogTier.Global))
                {
                    _registry.Publish(slot.Id, HealthVerdict.Suppressed, DateTime.UtcNow - slot.LastSeen,
                        "L3 熔断: VoWiFi 恢复预算耗尽。");
                }
            }
        }
        catch (Exception ex)
        {
            _eventBus?.Publish(EventTopics.SystemLog, "GlobalWatchdog",
                $"卡槽 [{slot.Name}] L3 VoWiFi 恢复异常: {ex.Message}");
        }
        finally
        {
            _voWifiRecoveryInFlight.TryRemove(slot.Id, out _);
        }
    }

    private long _lastRescanUtcTick;

    private async Task RescanPoolAsync(ModemSlot slot)
    {
        // One full rescan per 5 minutes at most — serial probing is expensive.
        var now = Environment.TickCount64;
        if (Volatile.Read(ref _lastRescanUtcTick) != 0 && now - Volatile.Read(ref _lastRescanUtcTick) < 300_000)
            return;
        Volatile.Write(ref _lastRescanUtcTick, now);

        _registry.RecordRecovery(slot.Id, "L3 触发全量设备重扫描（硬件升级无法在 L2 内消化）。");
        try
        {
            await _kernel.DiscoverSlotsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _eventBus?.Publish(EventTopics.SystemLog, "GlobalWatchdog",
                $"L3 全量重扫描失败: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

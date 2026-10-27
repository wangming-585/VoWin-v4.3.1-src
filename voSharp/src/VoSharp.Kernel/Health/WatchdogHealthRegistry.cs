using System.Collections.Concurrent;
using VoSharp.Common.Events;

namespace VoSharp.Kernel.Health;

/// <summary>
/// Identifies which watchdog tier owns a repair budget.
/// </summary>
public enum WatchdogTier
{
    /// <summary>L1 slot watchdog: lightweight self-repair (5 s cadence).</summary>
    Slot,

    /// <summary>L2 pool watchdog: hardware reconnection (15 s cadence).</summary>
    Pool,

    /// <summary>L3 global watchdog: VoWiFi restart decisions (30 s cadence).</summary>
    Global
}

/// <summary>
/// Shared, thread-safe health ledger for the three-tier watchdog hierarchy.
/// L1 writes observations and verdicts, L2 consumes pool escalations and
/// records hardware repairs, L3 consumes global escalations and applies the
/// circuit breaker. Every tier operates on per-slot sliding-window budgets so
/// a physically absent device can never trigger a recovery storm.
/// </summary>
public sealed class WatchdogHealthRegistry
{
    // Budget defaults: (window, max repair actions). Tuned so transient faults
    // retry quickly while a dead device goes quiet within a couple of minutes.
    public static readonly TimeSpan SlotBudgetWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan PoolBudgetWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan GlobalBudgetWindow = TimeSpan.FromMinutes(45);

    private const int SlotBudgetActions = 3;
    private const int PoolBudgetActions = 3;
    private const int GlobalBudgetActions = 40;

    /// <summary>Consecutive unhealthy L1 cycles before the slot escalates to the pool watchdog.</summary>
    public const int PoolEscalationThreshold = 3;

    private sealed record SlotLedger(
        WatchdogBudget SlotBudget,
        WatchdogBudget PoolBudget,
        WatchdogBudget GlobalBudget)
    {
        public HealthVerdict Verdict { get; set; } = HealthVerdict.Healthy;
        public int ConsecutiveFailures { get; set; }
        public TimeSpan DataAge { get; set; }
        public string? LastRecoveryAction { get; set; }
        public DateTime? LastRecoveryTime { get; set; }
        public EscalationLevel Escalation { get; set; } = EscalationLevel.None;
        public string? Detail { get; set; }
    }

    private readonly ConcurrentDictionary<string, SlotLedger> _ledgers = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncEventBus? _eventBus;

    /// <summary>Raised whenever a slot's health snapshot is republished.</summary>
    public event EventHandler<SlotHealth>? HealthChanged;

    public WatchdogHealthRegistry(AsyncEventBus? eventBus = null)
    {
        _eventBus = eventBus;
    }

    private SlotLedger GetLedger(string slotId) =>
        _ledgers.GetOrAdd(slotId, static _ => new SlotLedger(
            new WatchdogBudget(SlotBudgetWindow, SlotBudgetActions),
            new WatchdogBudget(PoolBudgetWindow, PoolBudgetActions),
            new WatchdogBudget(GlobalBudgetWindow, GlobalBudgetActions)));

    /// <summary>Latest published snapshot for a slot, or null when never observed.</summary>
    public SlotHealth? Get(string slotId)
    {
        var ledger = _ledgers.TryGetValue(slotId, out var value) ? value : null;
        return ledger == null ? null : ToSnapshot(slotId, ledger);
    }

    /// <summary>Immutable snapshot of every tracked slot.</summary>
    public IReadOnlyList<SlotHealth> Snapshot() =>
        _ledgers.Select(kv => ToSnapshot(kv.Key, kv.Value)).ToList();

    /// <summary>
    /// Publishes a fresh observation for a slot. When any tier's repair budget
    /// is exhausted the verdict is reported as <see cref="HealthVerdict.Suppressed"/>
    /// so the UI reflects that the watchdog has stopped intervening.
    /// </summary>
    public void Publish(string slotId, HealthVerdict verdict, TimeSpan dataAge, string? detail)
    {
        var ledger = GetLedger(slotId);

        var suppressed =
            ledger.SlotBudget.IsExhausted() ||
            ledger.PoolBudget.IsExhausted() ||
            ledger.GlobalBudget.IsExhausted();

        lock (ledger)
        {
            ledger.Verdict = suppressed ? HealthVerdict.Suppressed : verdict;
            ledger.DataAge = dataAge;
            if (!string.IsNullOrWhiteSpace(detail)) ledger.Detail = detail;
            if (verdict == HealthVerdict.Healthy) ledger.ConsecutiveFailures = 0;
            else ledger.ConsecutiveFailures++;
        }

        var snapshot = ToSnapshot(slotId, ledger);
        try { HealthChanged?.Invoke(this, snapshot); } catch { }
    }

    /// <summary>Attempts to reserve one repair action for the given tier.</summary>
    public bool TryConsumeRepair(string slotId, WatchdogTier tier)
    {
        var budget = tier switch
        {
            WatchdogTier.Slot => GetLedger(slotId).SlotBudget,
            WatchdogTier.Pool => GetLedger(slotId).PoolBudget,
            _ => GetLedger(slotId).GlobalBudget
        };
        if (budget.IsExhausted()) return false;
        budget.RecordAction();
        return true;
    }

    /// <summary>True when the given tier may no longer act on this slot.</summary>
    public bool IsBudgetExhausted(string slotId, WatchdogTier tier)
    {
        var budget = tier switch
        {
            WatchdogTier.Slot => GetLedger(slotId).SlotBudget,
            WatchdogTier.Pool => GetLedger(slotId).PoolBudget,
            _ => GetLedger(slotId).GlobalBudget
        };
        return budget.IsExhausted();
    }

    /// <summary>Records that a recovery action was taken (for diagnostics and UI).</summary>
    public void RecordRecovery(string slotId, string action)
    {
        var ledger = GetLedger(slotId);
        lock (ledger)
        {
            ledger.LastRecoveryAction = action;
            ledger.LastRecoveryTime = DateTime.UtcNow;
        }
        _eventBus?.Publish(EventTopics.SystemLog, "Watchdog", $"Slot {slotId}: {action}");
    }

    /// <summary>Sets the pending escalation target for a slot.</summary>
    public void SetEscalation(string slotId, EscalationLevel level)
    {
        var ledger = GetLedger(slotId);
        bool changed;
        lock (ledger)
        {
            changed = ledger.Escalation != level;
            ledger.Escalation = level;
        }
        if (changed)
        {
            try { HealthChanged?.Invoke(this, ToSnapshot(slotId, ledger)); } catch { }
        }
    }

    /// <summary>Clears a slot's circuit-breaker state (e.g. device returned or budget window slid).</summary>
    public void Reset(string slotId)
    {
        if (!_ledgers.TryGetValue(slotId, out var ledger)) return;
        lock (ledger)
        {
            ledger.ConsecutiveFailures = 0;
            ledger.Escalation = EscalationLevel.None;
        }
        ledger.SlotBudget.Reset();
        ledger.PoolBudget.Reset();
        ledger.GlobalBudget.Reset();
    }

    /// <summary>Forgets a slot that was removed from the pool.</summary>
    public void Forget(string slotId) => _ledgers.TryRemove(slotId, out _);

    private static SlotHealth ToSnapshot(string slotId, SlotLedger ledger)
    {
        lock (ledger)
        {
            return new SlotHealth(
                slotId,
                ledger.Verdict,
                ledger.DataAge,
                ledger.ConsecutiveFailures,
                ledger.LastRecoveryAction,
                ledger.LastRecoveryTime)
            {
                Escalation = ledger.Escalation
            };
        }
    }
}

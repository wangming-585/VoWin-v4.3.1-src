namespace VoSharp.Kernel.Health;

/// <summary>
/// Represents the health state of a slot as assessed by the watchdog hierarchy.
/// </summary>
public enum HealthVerdict
{
    /// <summary>Slot is operating normally, metrics are fresh.</summary>
    Healthy,

    /// <summary>Metrics are outdated (LastSeen aging), but slot is still structurally OK.</summary>
    Stale,

    /// <summary>Slot has detected issues (connection loss, SIM absent) but is attempting recovery.</summary>
    Degraded,

    /// <summary>Slot is offline, errored, or recovery has failed.</summary>
    Failed,

    /// <summary>Slot is in circuit-breaker mode: further recovery actions are suppressed.</summary>
    Suppressed
}

/// <summary>
/// Escalation level indicating which watchdog layer should handle the issue.
/// </summary>
public enum EscalationLevel
{
    /// <summary>No escalation needed, slot can self-heal.</summary>
    None,

    /// <summary>Escalate to L2 PoolWatchdog for hardware reconnection.</summary>
    ToPool,

    /// <summary>Escalate to L3 GlobalWatchdog for VoWiFi restart or circuit-breaker.</summary>
    ToGlobal
}

/// <summary>
/// Health snapshot for a single slot, produced by L1 SlotWatchdog.
/// </summary>
public sealed record SlotHealth(
    string SlotId,
    HealthVerdict Verdict,
    TimeSpan DataAge,
    int ConsecutiveFailures,
    string? LastRecoveryAction,
    DateTime? LastRecoveryTime)
{
    public EscalationLevel Escalation { get; init; } = EscalationLevel.None;
}

/// <summary>
/// Sliding-window circuit breaker to prevent recovery storms.
/// </summary>
public sealed class WatchdogBudget
{
    private readonly TimeSpan _windowDuration;
    private readonly int _maxActionsInWindow;
    private readonly Queue<DateTime> _recentActions = new();
    private readonly object _lock = new();

    public WatchdogBudget(TimeSpan windowDuration, int maxActionsInWindow)
    {
        _windowDuration = windowDuration;
        _maxActionsInWindow = maxActionsInWindow;
    }

    public bool IsExhausted()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - _windowDuration;
            while (_recentActions.Count > 0 && _recentActions.Peek() < cutoff)
                _recentActions.Dequeue();
            return _recentActions.Count >= _maxActionsInWindow;
        }
    }

    public void RecordAction()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - _windowDuration;
            while (_recentActions.Count > 0 && _recentActions.Peek() < cutoff)
                _recentActions.Dequeue();
            _recentActions.Enqueue(DateTime.UtcNow);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _recentActions.Clear();
        }
    }
}

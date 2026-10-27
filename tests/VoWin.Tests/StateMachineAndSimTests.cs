using System.IO;
using VoSharp.Common.Events;
using VoSharp.Sim;
using VoSharp.StateMachine;

namespace VoWin.Tests;

public class StateMachineTests
{
    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    [Fact]
    public void FullTransitionChain_PassesAllHomeStates()
    {
        var sm = new TelephonyStateMachine();

        Assert.True(sm.Fire(StateTrigger.TriggerSimReady));
        Assert.Equal(TelephonyState.SimReady, sm.CurrentState);

        Assert.True(sm.Fire(StateTrigger.TriggerNetSearch));
        Assert.Equal(TelephonyState.NetworkSearching, sm.CurrentState);

        Assert.True(sm.Fire(StateTrigger.TriggerNetAttach));
        Assert.Equal(TelephonyState.NetworkRegistered, sm.CurrentState);

        Assert.True(sm.Fire(StateTrigger.TriggerDataConnect));
        Assert.Equal(TelephonyState.DataConnecting, sm.CurrentState);

        Assert.True(sm.Fire(StateTrigger.TriggerDataConnected));
        Assert.Equal(TelephonyState.DataConnected, sm.CurrentState);

        Assert.True(sm.Fire(StateTrigger.TriggerImsRegister));
        Assert.Equal(TelephonyState.ImsRegistering, sm.CurrentState);

        Assert.True(sm.Fire(StateTrigger.TriggerImsRegistered));
        Assert.Equal(TelephonyState.ImsRegistered, sm.CurrentState);

        Assert.True(sm.Fire(StateTrigger.TriggerCallDial));
        Assert.Equal(TelephonyState.CallRinging, sm.CurrentState);

        Assert.True(sm.Fire(StateTrigger.TriggerCallAnswer));
        Assert.Equal(TelephonyState.CallActive, sm.CurrentState);

        Assert.True(sm.Fire(StateTrigger.TriggerCallHangup));
        Assert.Equal(TelephonyState.CallEnded, sm.CurrentState);

        Assert.True(sm.Fire(StateTrigger.TriggerReset));
        Assert.Equal(TelephonyState.ImsRegistered, sm.CurrentState);
    }

    [Theory]
    [InlineData(StateTrigger.TriggerCallDial)]
    [InlineData(StateTrigger.TriggerDataConnect)]
    [InlineData(StateTrigger.TriggerImsRegistered)]
    [InlineData(StateTrigger.TriggerReset)]
    public void IllegalTransition_ReturnsFalse_StateUnchanged(StateTrigger trigger)
    {
        var sm = new TelephonyStateMachine();
        // Init 状态只允许 SimReady / Error，其余应失败且保持 Init
        Assert.False(sm.Fire(trigger));
        Assert.Equal(TelephonyState.Init, sm.CurrentState);
    }

    [Fact]
    public void EnterExitHooks_CalledWithCorrectArguments()
    {
        var sm = new TelephonyStateMachine();
        sm.Fire(StateTrigger.TriggerSimReady, "sim-ctx");
        var hookList = new List<(TelephonyState, TelephonyState, object?)>();
        sm.OnEnter(TelephonyState.NetworkSearching, (f, t, p) => hookList.Add((f, t, p)));
        sm.Fire(StateTrigger.TriggerNetSearch, 42);
        Assert.Single(hookList);
        Assert.Equal(TelephonyState.SimReady, hookList[0].Item1);
        Assert.Equal(TelephonyState.NetworkSearching, hookList[0].Item2);
        Assert.Equal(42, hookList[0].Item3);
    }

    [Fact]
    public void History_RespectsLimit()
    {
        var sm = new TelephonyStateMachine(historyLimit: 2);
        sm.Fire(StateTrigger.TriggerSimReady);
        sm.Fire(StateTrigger.TriggerNetSearch);
        sm.Fire(StateTrigger.TriggerNetAttach);

        var history = sm.GetHistory();
        Assert.Equal(2, history.Count);
        Assert.Equal(TelephonyState.SimReady, history[0].From);
        Assert.Equal(TelephonyState.NetworkSearching, history[0].To);
        Assert.Equal(StateTrigger.TriggerNetSearch, history[0].Trigger);
        Assert.Equal(TelephonyState.NetworkSearching, history[1].From);
        Assert.Equal(TelephonyState.NetworkRegistered, history[1].To);
    }

    [Fact]
    public void StateChanged_EventFired_HandlerExceptionSwallowed()
    {
        var sm = new TelephonyStateMachine();
        var fired = 0;
        sm.StateChanged += (_, e) =>
        {
            fired++;
            Assert.Equal(TelephonyState.Init, e.From);
            Assert.Equal(TelephonyState.SimReady, e.To);
            Assert.Equal(StateTrigger.TriggerSimReady, e.Trigger);
            throw new InvalidOperationException("handler 异常不应中断 Fire");
        };

        Assert.True(sm.Fire(StateTrigger.TriggerSimReady));
        Assert.Equal(1, fired);
        Assert.Equal(TelephonyState.SimReady, sm.CurrentState);
    }

    [Fact]
    public void EventBusSimEvents_DriveStateMachine()
    {
        var bus = new AsyncEventBus();
        var sm = new TelephonyStateMachine();
        sm.ConnectEventBus(bus);

        bus.Publish(EventTopics.ModemSim, "test", "READY");
        Assert.True(WaitUntil(() => sm.CurrentState == TelephonyState.SimReady));

        bus.Publish(EventTopics.ModemSim, "test", "ABSENT");
        Assert.True(WaitUntil(() => sm.CurrentState == TelephonyState.Init));

        bus.Publish(EventTopics.ModemSim, "test", "SIM_INSERTED");
        Assert.True(WaitUntil(() => sm.CurrentState == TelephonyState.SimReady));

        _ = bus.DisposeAsync();
    }

    [Fact]
    public void EventBusNetworkAndCallEvents_DriveStateMachine()
    {
        var bus = new AsyncEventBus();
        var sm = new TelephonyStateMachine();
        sm.ConnectEventBus(bus);

        bus.Publish(EventTopics.ModemSim, "t", "READY");
        Assert.True(WaitUntil(() => sm.CurrentState == TelephonyState.SimReady));

        bus.Publish(EventTopics.NetworkRegistration, "t", "HOME");
        Assert.True(WaitUntil(() => sm.CurrentState == TelephonyState.NetworkRegistered));

        // NetworkRegistered 不支持搜索触发，LOST 允许回到搜索态
        bus.Publish(EventTopics.NetworkRegistration, "t", "LOST");
        Assert.True(WaitUntil(() => sm.CurrentState == TelephonyState.NetworkSearching));

        // NetworkSearching 下 HOME 触发 NetAttach 回到 Registered
        bus.Publish(EventTopics.NetworkRegistration, "t", "HOME");
        Assert.True(WaitUntil(() => sm.CurrentState == TelephonyState.NetworkRegistered));

        _ = bus.DisposeAsync();
    }
}

public class SimIdentityTests
{
    [Fact]
    public void FromImsiAndIccid_DefaultMncLength2()
    {
        var sim = SimIdentity.FromImsiAndIccid("460011234567890", "89860011223344556677");
        Assert.Equal("460", sim.Mcc);
        Assert.Equal("01", sim.Mnc);
        Assert.Equal("PLMN 460-01", sim.OperatorName);
        // 未显式提供 mncLength 时认为非权威（仅推测）
        Assert.False(sim.IsHomePlmnAuthoritative);
    }

    [Fact]
    public void FromImsiAndIccid_MncLength3()
    {
        var sim = SimIdentity.FromImsiAndIccid("310260123456789", "8901260123456789012", mncLength: 3);
        Assert.Equal("310", sim.Mcc);
        Assert.Equal("260", sim.Mnc);
    }

    [Fact]
    public void FromImsiAndIccid_BlankOperator_FallbackPlmn()
    {
        var sim = SimIdentity.FromImsiAndIccid("460001234567890", "iccid", opName: "   ");
        Assert.Equal("PLMN 460-00", sim.OperatorName);
    }

    [Fact]
    public void FromImsiAndIccid_InvalidMncLength_Fallback2()
    {
        var sim = SimIdentity.FromImsiAndIccid("460001234567890", "iccid", mncLength: 5);
        Assert.Equal("00", sim.Mnc);
        // 非法长度按2位回退，且视为非权威
        Assert.False(sim.IsHomePlmnAuthoritative);
    }

    [Fact]
    public void FromImsiAndIccid_HomePlmns_Distinct()
    {
        var sim = SimIdentity.FromImsiAndIccid(
            "460001234567890", "iccid", mncLength: 2,
            homePlmns: new[] { "460-00", "460-00", "460-11" });
        Assert.Equal(2, sim.HomePlmns!.Count);
        Assert.Equal(new[] { "460-00", "460-11" }, sim.HomePlmns);
    }

    [Fact]
    public void FromImsiAndIccid_ReportedPermanent_TrimAndNull()
    {
        var sim = SimIdentity.FromImsiAndIccid(
            "460001234567890", "iccid",
            reportedImsi: " 460001234567890 ", permanentImsi: "  ",
            imsiSource: " modem ");
        Assert.Equal("460001234567890", sim.ReportedImsi);
        Assert.Null(sim.PermanentImsi);
        Assert.Equal("modem", sim.ImsiSource);
    }
}

public class SimIdentityHistoryStoreTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vowin-simstore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "identities.json");
    }

    private static SimIdentity Identity(string imsi, string iccid, string? homePlmn = null)
    {
        var homePlmns = homePlmn is null ? null : new[] { homePlmn };
        return new SimIdentity(imsi, iccid, "460", "00", "PLMN 460-00",
            HomePlmns: homePlmns);
    }

    [Fact]
    public void Observe_ThenGetCandidates_ReturnsIdentity()
    {
        var path = TempPath();
        var store = new SimIdentityHistoryStore(path);
        var live = Identity("460001234567890", "ICC-1");
        store.Observe(live);

        var candidates = store.GetCandidates(live);
        Assert.Single(candidates);
        Assert.Equal("460001234567890", candidates[0].Imsi);
        Assert.Equal("ICC-1", candidates[0].Iccid);
    }

    [Fact]
    public void MarkSuccessful_PrioritizesSucceededEntries()
    {
        var path = TempPath();
        var store = new SimIdentityHistoryStore(path);
        var a = Identity("460001234567890", "ICC-1");
        var b = Identity("460111234567890", "ICC-1");
        store.Observe(a);
        store.MarkSuccessful(b);

        var candidates = store.GetCandidates(a);
        Assert.Equal("460111234567890", candidates[0].Imsi);
    }

    [Fact]
    public void InvalidImsi_NotStored()
    {
        var path = TempPath();
        var store = new SimIdentityHistoryStore(path);
        var bad = new SimIdentity("123", "ICC-1", "460", "00", "PLMN 460-00");
        store.Observe(bad);

        var candidates = store.GetCandidates(bad);
        Assert.Empty(candidates);
    }

    [Fact]
    public void CardKeyIsolation_ByIccid()
    {
        var path = TempPath();
        var store = new SimIdentityHistoryStore(path);
        store.Observe(Identity("460001234567890", "ICC-A"));
        var other = Identity("460111234567890", "ICC-B");

        var candidates = store.GetCandidates(other);
        Assert.Empty(candidates);
    }

    [Fact]
    public void Persistence_RoundTrip_Reconstruct()
    {
        var path = TempPath();
        var store1 = new SimIdentityHistoryStore(path);
        var live = Identity("460001234567890", "ICC-1", "460-00");
        store1.MarkSuccessful(live);

        var store2 = new SimIdentityHistoryStore(path);
        var candidates = store2.GetCandidates(live);
        Assert.Single(candidates);
        Assert.Equal("460001234567890", candidates[0].Imsi);
        Assert.Contains("460-00", candidates[0].HomePlmns!);
    }

    [Fact]
    public void Capacity_CappedAt8()
    {
        var path = TempPath();
        var store = new SimIdentityHistoryStore(path);
        var live = Identity("460001234567890", "ICC-1");
        for (var i = 0; i < 12; i++)
        {
            store.Observe(new SimIdentity($"460{i:D10}", "ICC-1", "460", "00", "PLMN 460-00"));
        }

        var candidates = store.GetCandidates(live);
        Assert.True(candidates.Count <= 8);
    }
}

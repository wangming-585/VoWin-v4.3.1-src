﻿using VoSharp.Common.Events;
using VoSharp.Euicc.Download;
using VoSharp.Euicc.Models;
using VoSharp.Euicc.Profiles;
using VoSharp.Euicc.Sgp22;
using VoSharp.Euicc.Transport;

namespace VoSharp.Euicc;

/// <summary>
/// High-level manager for eUICC / eSIM operations.
/// Implements the GSMA SGP.22 ES10 (LPA-eUICC interface) profile management subset
/// (GetProfilesInfo, EnableProfile, DisableProfile, DeleteProfile, SetNickname, GetEuiccInfo).
/// Remote profile download is implemented in-process with .NET HTTPS and ES10 APDU relay.
/// </summary>
public class EuiccManager
{
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    public const string XeSimIsdrAid = "A0000005591010FFFFFFFF8900000177";
    public const string EstkProductAid = "A06573746B6D65FFFFFFFFFFFF6D6774";
    public const string EstkSe0IsdrAid = "A06573746B6D65FFFF4953442D522030";
    public const string EstkSe1IsdrAid = "A06573746B6D65FFFF4953442D522031";

    public IEuiccTransport Transport { get; }
    public AsyncEventBus? EventBus { get; }
    public string DefaultAid { get; set; } = Sgp22Client.IsdrAidStandard;

    public event EventHandler<EuiccProfilesChangedEventArgs>? ProfilesUpdated;
    public event EventHandler<EuiccOperationEventArgs>? OperationCompleted;

    public EuiccManager(IEuiccTransport transport, AsyncEventBus? eventBus = null)
    {
        Transport = transport;
        EventBus = eventBus;
    }

    public async Task<T> ExecuteSessionAsync<T>(Func<Sgp22Client, Task<T>> action, CancellationToken ct = default)
        => await ExecuteSessionForAidAsync(DefaultAid, action, ct).ConfigureAwait(false);

    public async Task<T> ExecuteSessionAsync<T>(string aid, Func<Sgp22Client, Task<T>> action, CancellationToken ct = default)
        => await ExecuteSessionForAidAsync(ResolveAid(aid), action, ct).ConfigureAwait(false);

    private async Task<T> ExecuteSessionForAidAsync<T>(string aid, Func<Sgp22Client, Task<T>> action, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int channel = await Transport.OpenLogicalChannelAsync(aid, ct).ConfigureAwait(false);
            try
            {
                var client = new Sgp22Client(Transport, channel);
                return await action(client).ConfigureAwait(false);
            }
            finally
            {
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Transport.CloseLogicalChannelAsync(channel, cleanupCts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task ExecuteSessionAsync(Func<Sgp22Client, Task> action, CancellationToken ct = default)
        => await ExecuteSessionForAidAsync(DefaultAid, action, ct).ConfigureAwait(false);

    public async Task ExecuteSessionAsync(string aid, Func<Sgp22Client, Task> action, CancellationToken ct = default)
        => await ExecuteSessionForAidAsync(ResolveAid(aid), action, ct).ConfigureAwait(false);

    private async Task ExecuteSessionForAidAsync(string aid, Func<Sgp22Client, Task> action, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int channel = await Transport.OpenLogicalChannelAsync(aid, ct).ConfigureAwait(false);
            try
            {
                var client = new Sgp22Client(Transport, channel);
                await action(client).ConfigureAwait(false);
            }
            finally
            {
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Transport.CloseLogicalChannelAsync(channel, cleanupCts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public Task<string> GetEIDAsync(CancellationToken ct = default)
    {
        return ExecuteSessionAsync(client => client.GetEIDAsync(ct), ct);
    }

    /// <summary>Returns all EID candidates exposed by the currently addressed eUICC transport.</summary>
    public Task<IReadOnlyList<string>> GetEidsAsync(CancellationToken ct = default)
    {
        return ExecuteSessionAsync(client => client.GetEidsAsync(ct), ct);
    }

    public Task<List<Profile>> ListProfilesAsync(CancellationToken ct = default)
        => ListProfilesAsync(DefaultAid, ct);

    public async Task<List<Profile>> ListProfilesAsync(string? euiccAid, CancellationToken ct = default)
    {
        var aid = ResolveAid(euiccAid);
        var profiles = await ExecuteSessionForAidAsync(aid, client => client.GetProfilesInfoAsync(ct), ct).ConfigureAwait(false);
        foreach (var profile in profiles)
        {
            profile.EuiccAid = aid;
            profile.IsCurrentlyAddressable = true;
        }
        try { ProfilesUpdated?.Invoke(this, new EuiccProfilesChangedEventArgs(profiles)); } catch { }
        EventBus?.Publish("euicc.profiles.listed", "EuiccManager", profiles);
        return profiles;
    }

    public async Task<Profile?> GetActiveProfileAsync(CancellationToken ct = default)
    {
        var list = await ListProfilesAsync(ct).ConfigureAwait(false);
        return list.FirstOrDefault(p => p.State == ProfileState.Enabled);
    }

    public async Task<Profile?> GetActiveProfileAsync(string? euiccAid, CancellationToken ct = default)
    {
        var list = await ListProfilesAsync(euiccAid, ct).ConfigureAwait(false);
        return list.FirstOrDefault(p => p.State == ProfileState.Enabled);
    }

    public async Task<IReadOnlyList<EuiccInventoryEntry>> GetInventoryAsync(CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var aids = await DiscoverEuiccAidsWithoutGateAsync(ct).ConfigureAwait(false);
            var entries = new List<EuiccInventoryEntry>();
            Exception? lastError = null;
            foreach (var aid in aids)
            {
                try
                {
                    var entry = await ReadInventoryEntryWithoutGateAsync(aid, ct).ConfigureAwait(false);
                    if (!entries.Any(existing =>
                        !string.IsNullOrWhiteSpace(entry.Eid) &&
                        existing.Eid.Equals(entry.Eid, StringComparison.OrdinalIgnoreCase)))
                        entries.Add(entry);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                }
            }

            if (entries.Count > 0) return entries;
            throw lastError ?? new InvalidOperationException("未检测到可用 eUICC ISD-R 应用。");
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> DiscoverEuiccAidsWithoutGateAsync(CancellationToken ct)
    {
        // eSTK cards expose two independent secure elements. Do not append
        // standard AIDs after finding them: the standard one aliases an eSTK storage.
        if (await CanOpenAidWithoutGateAsync(EstkProductAid, ct).ConfigureAwait(false))
        {
            var estk = new List<string>();
            foreach (var aid in new[] { EstkSe0IsdrAid, EstkSe1IsdrAid })
            {
                if (await CanOpenAidWithoutGateAsync(aid, ct).ConfigureAwait(false)) estk.Add(aid);
            }
            if (estk.Count > 0) return estk;
        }

        var generic = new List<string>();
        foreach (var aid in new[] { Sgp22Client.IsdrAidStandard, XeSimIsdrAid })
        {
            if (await CanOpenAidWithoutGateAsync(aid, ct).ConfigureAwait(false)) generic.Add(aid);
        }
        return generic;
    }

    private async Task<bool> CanOpenAidWithoutGateAsync(string aid, CancellationToken ct)
    {
        try
        {
            var channel = await Transport.OpenLogicalChannelAsync(aid, ct).ConfigureAwait(false);
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Transport.CloseLogicalChannelAsync(channel, cleanupCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch { return false; }
    }

    private async Task<EuiccInventoryEntry> ReadInventoryEntryWithoutGateAsync(string aid, CancellationToken ct)
    {
        var channel = await Transport.OpenLogicalChannelAsync(aid, ct).ConfigureAwait(false);
        try
        {
            var client = new Sgp22Client(Transport, channel);
            string eid;
            try { eid = await client.GetEIDAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A few older cards expose ES10.GetProfilesInfo but omit EID.
                // Keep that independently selected AID usable instead of
                // misreporting an eUICC as an ordinary SIM.
                eid = string.Empty;
            }
            var profiles = await client.GetProfilesInfoAsync(ct).ConfigureAwait(false);
            foreach (var profile in profiles)
            {
                if (!string.IsNullOrWhiteSpace(eid)) profile.Eid = eid;
                profile.EuiccAid = aid;
                profile.IsCurrentlyAddressable = true;
            }
            return new EuiccInventoryEntry(eid, aid, profiles);
        }
        finally
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Transport.CloseLogicalChannelAsync(channel, cleanupCts.Token).ConfigureAwait(false);
        }
    }

    private string ResolveAid(string? aid) => string.IsNullOrWhiteSpace(aid) ? DefaultAid : aid.Trim().ToUpperInvariant();

    public Task SwitchProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default)
        => SwitchProfileAsync(iccidOrAid, null, refresh, ct, null, ProfileSwitchMode.EnableWithFallback);

    /// <summary>
    /// Activates one installed profile and proves the switch took effect.
    /// <paramref name="iccidOrAid"/> accepts any selector understood by
    /// <see cref="ProfileSelector"/>: ICCID, 1-based index, ISD-P AID, nickname,
    /// profile name, or the keywords next / prev / active.
    /// Every phase reports Chinese progress with an explicit step counter, and a
    /// failure is reported as <see cref="ProfileSwitchProgress.Failed"/> before the
    /// exception leaves this method, so a caller can never mistake a failure for
    /// an unfinished switch.
    /// </summary>
    public async Task SwitchProfileAsync(
        string iccidOrAid,
        string? euiccAid,
        bool refresh = true,
        CancellationToken ct = default,
        IProgress<ProfileSwitchProgress>? progress = null,
        ProfileSwitchMode mode = ProfileSwitchMode.EnableWithFallback)
    {
        var aid = ResolveAid(euiccAid);
        // The step total is fixed up front so "[3/8]" never jumps backwards mid-run.
        var totalSteps = mode == ProfileSwitchMode.DisableOthersFirst ? 8 : 7;

        void Report(int step, int percent, string stage, string? detail = null, bool failed = false)
            => progress?.Report(new ProfileSwitchProgress(percent, stage, detail, step, totalSteps, failed));

        Report(1, 5, "读取卡内 Profile 列表");
        try
        {
            var profiles = await ExecuteSessionForAidAsync(aid, client => client.GetProfilesInfoAsync(ct), ct)
                .ConfigureAwait(false);
            if (profiles.Count == 0)
                throw new InvalidOperationException("卡内没有任何已安装的 Profile，无法执行切换。");

            Report(2, 15, "定位目标 Profile");
            var resolution = ProfileSelector.Resolve(iccidOrAid, profiles);
            if (!resolution.Success || resolution.Profile is null)
            {
                var reason = resolution.Error ?? "无法确定目标 Profile。";
                Report(2, 15, "定位目标 Profile 失败", reason, failed: true);
                throw new InvalidOperationException(reason);
            }

            var target = resolution.Profile;
            var targetId = string.IsNullOrWhiteSpace(target.ICCID) ? target.ISDPAID : target.ICCID;
            var targetLabel = DescribeProfile(target);
            EventBus?.Publish("euicc.profile.switch.started", "EuiccManager",
                new { Target = targetId, Label = targetLabel, Mode = mode.ToString() });

            Report(3, 25, "检查当前启用状态",
                $"{targetLabel} 当前为 {(target.State == ProfileState.Enabled ? "已启用" : "未启用")}");
            if (target.State == ProfileState.Enabled)
            {
                // Already on the requested profile: report completion instead of
                // sending a redundant EnableProfile that would reset the card.
                Report(totalSteps, 100, "目标 Profile 已处于启用状态，无需切换", targetLabel);
                try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Switch", targetIccidOrAid: targetId, success: true)); } catch { }
                EventBus?.Publish("euicc.profile.switched", "EuiccManager", targetId);
                return;
            }

            if (mode == ProfileSwitchMode.DisableOthersFirst)
            {
                Report(4, 35, "停用其他已启用的 Profile");
                var disabled = await DisableOtherProfilesAsync(targetId, aid, ct).ConfigureAwait(false);
                Report(4, 40, "已停用其他 Profile", $"共停用 {disabled} 个");

                // Disabling also makes some eUICCs start a REFRESH procedure, so
                // clear that lock before the EnableProfile attempt instead of
                // wasting one of the three attempts on a card that cannot answer.
                if (disabled > 0 && Transport is IEuiccCardResettable afterDisable)
                {
                    Report(4, 42, "断电复位解除停用引发的 REFRESH 锁定");
                    try { afterDisable.ResetCard(); } catch { }
                    await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
                }
            }

            // Redundant switch flow: enable + verify in up to 3 sessions. A SIM
            // REFRESH may reset the card mid-operation, so each attempt reopens the
            // channel and re-reads profile state instead of trusting the response.
            const int maxAttempts = 3;
            var backoffMs = new[] { 400, 800, 1600 };
            Exception? lastError = null;
            var enableStep = mode == ProfileSwitchMode.DisableOthersFirst ? 5 : 4;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                Report(enableStep, 45, "发送 EnableProfile 指令",
                    $"第 {attempt}/{maxAttempts} 次尝试，refresh={refresh}");

                try
                {
                    await ExecuteSessionForAidAsync(aid, async client =>
                        await client.EnableProfileAsync(targetId, refresh, ct).ConfigureAwait(false), ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Enable may fail after the card already committed the change.
                    // Verification below decides the real outcome, so only record it.
                    lastError = ex;
                    Report(enableStep, 47, "EnableProfile 返回异常，转入状态核验", ex.Message);
                }

                // EnableProfile(refresh=true) leaves the card waiting for a
                // terminal acknowledgement of the REFRESH that a PC/SC reader
                // never sends; until power is cycled it answers 6985 to every
                // command. Measured on this reader: a power cycle clears the
                // lock immediately and keeps the new profile active.
                if (Transport is IEuiccCardResettable resettable)
                {
                    Report(enableStep + 1, 60, "断电复位解除 SIM REFRESH 锁定",
                        "PC/SC 读卡器无法发送 REFRESH 确认，需复位卡片");
                    try
                    {
                        resettable.ResetCard();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Report(enableStep + 1, 62, "断电复位失败", ex.Message);
                    }
                }
                else
                {
                    Report(enableStep + 1, 60, "等待 SIM REFRESH 完成", $"{400}ms");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);

                Report(enableStep + 2, 75, "重新打开 ISD-R 会话验证状态");
                bool? enabled;
                try
                {
                    // The card may still be finishing its reset right now, so the
                    // verification retries internally instead of reporting a bogus
                    // "not enabled" that would restart the whole attempt loop.
                    enabled = await WaitForProfileStateAsync(targetId, aid, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    enabled = null;
                }

                if (enabled == true)
                {
                    // Wording matters here: this is the eUICC layer confirming the
                    // profile, not the end of the whole switch. An outer caller
                    // (ModemSlot) still has to reload the SIM identity afterwards,
                    // so claiming "切卡成功" at this point would be misleading.
                    Report(totalSteps, 100, "eUICC 已确认目标 Profile 处于启用状态", targetLabel);
                    try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Switch", targetIccidOrAid: targetId, success: true)); } catch { }
                    EventBus?.Publish("euicc.profile.switched", "EuiccManager", targetId);
                    return;
                }

                if (attempt < maxAttempts)
                {
                    var delay = backoffMs[attempt - 1];
                    Report(enableStep, 45, $"第 {attempt} 次未生效，{delay}ms 后重试",
                        enabled is null ? "状态读取失败" : "目标仍为未启用");
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), ct).ConfigureAwait(false);
                }
            }

            // Fallback: some eUICCs refuse to enable a second profile while another
            // one stays enabled, so clear the others and retry exactly once.
            Report(enableStep, 45, "回退方案：停用其他已启用 Profile 后重试");
            try
            {
                var disabled = await DisableOtherProfilesAsync(targetId, aid, ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
                Report(enableStep, 50, "回退方案：重新发送 EnableProfile", $"已停用 {disabled} 个其他 Profile");
                await ExecuteSessionForAidAsync(aid, async client =>
                    await client.EnableProfileAsync(targetId, refresh, ct).ConfigureAwait(false), ct)
                    .ConfigureAwait(false);
                if (Transport is IEuiccCardResettable fallbackReset)
                {
                    Report(enableStep + 1, 62, "回退方案：断电复位解除 REFRESH 锁定");
                    try { fallbackReset.ResetCard(); } catch { }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
                Report(enableStep + 2, 75, "回退方案：核验启用状态");
                if (await WaitForProfileStateAsync(targetId, aid, ct).ConfigureAwait(false) == true)
                {
                    Report(totalSteps, 100, "eUICC 已确认目标 Profile 启用（经回退方案）", targetLabel);
                    try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Switch", targetIccidOrAid: targetId, success: true)); } catch { }
                    EventBus?.Publish("euicc.profile.switched", "EuiccManager", targetId);
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }

            var failure = $"切卡到 {targetLabel} 失败：{maxAttempts} 次尝试与回退方案均未使目标进入启用状态"
                + (lastError is null ? "。" : $"；最后一次错误：{lastError.Message}");
            Report(totalSteps, 100, "切卡失败", failure, failed: true);
            throw new InvalidOperationException(failure, lastError);
        }
        catch (Exception ex)
        {
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Switch", targetIccidOrAid: iccidOrAid, success: false, errorMessage: ex.Message)); } catch { }
            throw;
        }
    }

    /// <summary>Short human-readable label: nickname, else name, else masked ICCID.</summary>
    private static string DescribeProfile(Profile profile)
    {
        var name = !string.IsNullOrWhiteSpace(profile.Nickname) ? profile.Nickname
                 : !string.IsNullOrWhiteSpace(profile.ProfileName) ? profile.ProfileName
                 : !string.IsNullOrWhiteSpace(profile.ServiceProviderName) ? profile.ServiceProviderName
                 : null;
        var iccid = profile.ICCID.Length > 4 ? "…" + profile.ICCID[^4..] : profile.ICCID;
        return name is null ? $"ICCID {iccid}" : $"{name} (ICCID {iccid})";
    }

    /// <summary>
    /// Reads the target's enabled state, tolerating a card that is still
    /// restarting after SIM REFRESH. Returns null only when the state truly
    /// cannot be read within the retry budget.
    /// </summary>
    private async Task<bool?> WaitForProfileStateAsync(string iccidOrAid, string aid, CancellationToken ct)
    {
        const int attempts = 6;
        for (var i = 1; i <= attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = await IsProfileEnabledAsync(iccidOrAid, aid, ct).ConfigureAwait(false);
                if (state is not null) return state;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (i == attempts) throw;
            }

            if (i < attempts)
                await Task.Delay(TimeSpan.FromMilliseconds(250 * i), ct).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<bool?> IsProfileEnabledAsync(string iccidOrAid, string aid, CancellationToken ct)
    {
        var profiles = await ExecuteSessionForAidAsync(aid, async client =>
            await client.GetProfilesInfoAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        var target = profiles.FirstOrDefault(p =>
            string.Equals(p.ICCID, iccidOrAid, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.ISDPAID, iccidOrAid, StringComparison.OrdinalIgnoreCase));
        return target is null ? null : target.State == ProfileState.Enabled;
    }

    /// <summary>
    /// Disables every enabled profile except the target. Returns how many were
    /// disabled so the caller can report a concrete count instead of "done".
    /// </summary>
    private async Task<int> DisableOtherProfilesAsync(string iccidOrAid, string aid, CancellationToken ct)
    {
        var profiles = await ExecuteSessionForAidAsync(aid, async client =>
            await client.GetProfilesInfoAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        var count = 0;
        foreach (var p in profiles.Where(p => p.State == ProfileState.Enabled))
        {
            if (string.Equals(p.ICCID, iccidOrAid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.ISDPAID, iccidOrAid, StringComparison.OrdinalIgnoreCase))
                continue;
            var victim = p.ICCID.Length > 0 ? p.ICCID : p.ISDPAID;
            await ExecuteSessionForAidAsync(aid, async client =>
                await client.DisableProfileAsync(victim, refresh: false, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    public Task DisableProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default)
        => DisableProfileAsync(iccidOrAid, null, refresh, ct);

    public async Task DisableProfileAsync(string iccidOrAid, string? euiccAid, bool refresh = true, CancellationToken ct = default)
    {
        try
        {
            await ExecuteSessionForAidAsync(ResolveAid(euiccAid), client => client.DisableProfileAsync(iccidOrAid, refresh, ct), ct).ConfigureAwait(false);
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Disable", targetIccidOrAid: iccidOrAid, success: true)); } catch { }
            EventBus?.Publish("euicc.profile.disabled", "EuiccManager", iccidOrAid);
        }
        catch (Exception ex)
        {
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Disable", targetIccidOrAid: iccidOrAid, success: false, errorMessage: ex.Message)); } catch { }
            throw;
        }
    }

    public Task DeleteProfileAsync(string iccidOrAid, CancellationToken ct = default)
        => DeleteProfileAsync(iccidOrAid, null, ct);

    public async Task DeleteProfileAsync(string iccidOrAid, string? euiccAid, CancellationToken ct = default)
    {
        try
        {
            await ExecuteSessionForAidAsync(ResolveAid(euiccAid), client => client.DeleteProfileAsync(iccidOrAid, ct), ct).ConfigureAwait(false);
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Delete", targetIccidOrAid: iccidOrAid, success: true)); } catch { }
            EventBus?.Publish("euicc.profile.deleted", "EuiccManager", iccidOrAid);
        }
        catch (Exception ex)
        {
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Delete", targetIccidOrAid: iccidOrAid, success: false, errorMessage: ex.Message)); } catch { }
            throw;
        }
    }

    public Task RenameProfileAsync(string iccidOrAid, string nickname, CancellationToken ct = default)
        => RenameProfileAsync(iccidOrAid, nickname, null, ct);

    public async Task RenameProfileAsync(string iccidOrAid, string nickname, string? euiccAid, CancellationToken ct = default)
    {
        try
        {
            await ExecuteSessionForAidAsync(ResolveAid(euiccAid), client => client.SetNicknameAsync(iccidOrAid, nickname, ct), ct).ConfigureAwait(false);
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Rename", targetIccidOrAid: iccidOrAid, nickname: nickname, success: true)); } catch { }
            EventBus?.Publish("euicc.profile.renamed", "EuiccManager", new { Target = iccidOrAid, Nickname = nickname });
        }
        catch (Exception ex)
        {
            try { OperationCompleted?.Invoke(this, new EuiccOperationEventArgs("Rename", targetIccidOrAid: iccidOrAid, nickname: nickname, success: false, errorMessage: ex.Message)); } catch { }
            throw;
        }
    }

    public Task<EuiccInfo> GetEuiccInfoAsync(CancellationToken ct = default)
    {
        return ExecuteSessionAsync(client => client.GetEuiccInfoAsync(ct), ct);
    }

    public async Task<EuiccDownloadResult> DownloadProfileAsync(
        string activationCode,
        string imei,
        string? confirmationCode = null,
        IProgress<EuiccDownloadProgress>? progress = null,
        CancellationToken ct = default,
        bool allowUntrustedTls = false,
        bool allowRetryAfterUncertain = false,
        string? euiccAid = null)
    {
        var targetAid = ResolveAid(euiccAid);
        var parsedCode = EuiccActivationCode.Parse(activationCode);
        var normalizedImei = new string((imei ?? string.Empty).Where(char.IsDigit).ToArray());
        if (!IsValidImei(normalizedImei))
            throw new ArgumentException("模组 IMEI 必须是校验位正确的 15 位号码，无法执行 eSIM 服务器认证。", nameof(imei));
        var normalizedConfirmationCode = confirmationCode?.Trim() ?? string.Empty;
        if (parsedCode.ConfirmationCodeRequired && string.IsNullOrEmpty(normalizedConfirmationCode))
            throw new ArgumentException("该激活码要求确认码，请填写运营商提供的确认码。", nameof(confirmationCode));
        if (normalizedConfirmationCode.Length > 64 || normalizedConfirmationCode.Any(char.IsControl))
            throw new ArgumentException("确认码长度或字符无效。", nameof(confirmationCode));

        var fingerprint = EuiccDownloadJournal.Fingerprint(parsedCode.CanonicalCode);
        var journalStarted = false;
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            progress?.Report(new EuiccDownloadProgress(5, "正在读取 eUICC 并执行写入前检查"));
            var before = await ListProfilesWithoutGateAsync(targetAid, ct).ConfigureAwait(false);
            var beforeIccids = before.Select(p => p.ICCID).Where(v => !string.IsNullOrWhiteSpace(v)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (EuiccDownloadJournal.Get(fingerprint) is { } previousAttempt)
            {
                var previouslyInstalled = before.FirstOrDefault(profile =>
                    (!string.IsNullOrWhiteSpace(previousAttempt.InstalledIccid) && profile.ICCID.Equals(previousAttempt.InstalledIccid, StringComparison.OrdinalIgnoreCase)) ||
                    !previousAttempt.ProfilesBefore.Contains(profile.ICCID, StringComparer.OrdinalIgnoreCase));
                if (previouslyInstalled is not null)
                {
                    return new EuiccDownloadResult(
                        previouslyInstalled.ICCID,
                        true,
                        "检测到该激活码对应的 Profile 已在卡内；已阻止重复下载。请在安装通知列表中确认运营商通知状态。");
                }
                if (allowRetryAfterUncertain)
                {
                    EuiccDownloadJournal.ResetForAuthorizedRetry(fingerprint);
                    progress?.Report(new EuiccDownloadProgress(7, "服务商已授权重试；已清除本地事务锁定，正在建立新下载事务"));
                }
                else
                {
                    throw new EuiccDownloadUncertainException(
                        "该激活码已有未完成或结果不确定的事务记录。为防止 SM-DP+ 已消耗二维码，已禁止再次下载；请先重新读取卡片和安装通知。",
                        cardCommitMayHaveCompleted: true);
                }
            }

            try
            {
                var info = await GetEuiccInfoWithoutGateAsync(targetAid, ct).ConfigureAwait(false);
                if (info.FreeNvramBytes is > 0 and < 81_920)
                    throw new InvalidOperationException($"eUICC 剩余空间仅 {info.FreeNvramBytes} 字节，低于 80 KB 安全阈值；请先删除不用的 Profile。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ex.Message.Contains("80 KB", StringComparison.Ordinal))
            {
                // Some older eUICCs do not expose free-NVRAM in GetEuiccInfo1.
                // A successful profile list still proves that the ISD-R is usable.
            }

            EuiccDownloadJournal.Begin(fingerprint, beforeIccids);
            journalStarted = true;

            var downloader = new EuiccProfileDownloader(Transport, allowUntrustedTls);
            Exception? downloadError = null;
            string reportedIccid = string.Empty;
            EuiccWorkerDownloadResult? workerResult = null;
            var lastDownloadProgress = new EuiccDownloadProgress(10, "正在建立下载会话");
            var trackedProgress = new EuiccForwardingProgress(value =>
            {
                lastDownloadProgress = value;
                progress?.Report(value);
            });
            try
            {
                workerResult = await downloader.DownloadAsync(parsedCode, normalizedImei, normalizedConfirmationCode, targetAid, trackedProgress, ct).ConfigureAwait(false);
                reportedIccid = workerResult.Iccid;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                downloadError = ex;
            }

            progress?.Report(downloadError is null
                ? new EuiccDownloadProgress(95, "正在重新读取 Profile 并核对写入结果")
                : new EuiccDownloadProgress(
                    lastDownloadProgress.Percent,
                    $"{lastDownloadProgress.Status}时失败；正在只读核验卡片，确认是否发生写入"));
            List<Profile>? after = null;
            Exception? verifyError = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    after = await ListProfilesWithoutGateAsync(targetAid, ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    verifyError = ex;
                }
            }

            var newProfiles = after?
                .Where(p => !string.IsNullOrWhiteSpace(p.ICCID) && !beforeIccids.Contains(p.ICCID))
                .ToList() ?? [];
            var installed = !string.IsNullOrWhiteSpace(reportedIccid)
                ? newProfiles.SingleOrDefault(p => p.ICCID.Equals(reportedIccid, StringComparison.OrdinalIgnoreCase))
                : newProfiles.Count == 1 ? newProfiles[0] : null;

            if (installed is null)
            {
                if (downloadError is EuiccProfileDownloaderException { RetrySafe: false } unsafeError)
                {
                    EuiccDownloadJournal.MarkUncertain(fingerprint);
                    var state = unsafeError.Ambiguous
                        ? "写卡数据传输已经开始，但未收到完整安装结果，当前无法确认卡内是否已经提交"
                        : "下载事务已经进入运营商处理阶段";
                    throw new EuiccDownloadUncertainException(
                        $"{state}；此激活码可能已经失效，禁止直接再次下载。请先重新读取 Profile 和安装通知。原始错误：{unsafeError.Message}",
                        unsafeError.Ambiguous,
                        unsafeError);
                }
                if (downloadError is not null)
                {
                    EuiccDownloadJournal.RemoveIfRetrySafe(fingerprint);
                    journalStarted = false;
                    throw downloadError;
                }
                if (verifyError is not null)
                {
                    EuiccDownloadJournal.MarkUncertain(fingerprint);
                    throw new EuiccDownloadUncertainException(
                        $"Profile 写入流程完成，但无法重新读取卡片确认结果：{verifyError.Message}。已禁止直接重试该激活码。",
                        cardCommitMayHaveCompleted: true,
                        verifyError);
                }
                EuiccDownloadJournal.MarkUncertain(fingerprint);
                throw new EuiccDownloadUncertainException(
                    "Profile 写入流程完成，但卡片列表中未出现可核验的新 Profile。激活码可能已经被消耗，已禁止直接重试；请先重新读取卡片和安装通知。",
                    cardCommitMayHaveCompleted: true);
            }


            EuiccDownloadJournal.Complete(fingerprint, installed.ICCID);
            journalStarted = false;

            try { ProfilesUpdated?.Invoke(this, new EuiccProfilesChangedEventArgs(after!)); } catch { }
            EventBus?.Publish("euicc.profile.downloaded", "EuiccManager", new { installed.ICCID, parsedCode.SmdpAddress });

            if (downloadError is not null || workerResult?.Recovered == true || !string.IsNullOrWhiteSpace(workerResult?.Warning))
            {
                var warning = workerResult?.Warning ?? $"Profile 已写入卡片，但运营商确认阶段返回警告：{downloadError?.Message}";
                return new EuiccDownloadResult(installed.ICCID, true, warning);
            }

            progress?.Report(new EuiccDownloadProgress(100, "Profile 已写入并通过卡片列表核验"));
            return new EuiccDownloadResult(installed.ICCID);
        }
        finally
        {
            if (journalStarted)
            {
                try { EuiccDownloadJournal.MarkUncertain(fingerprint); } catch { }
            }
            _sessionGate.Release();
        }
    }

    private async Task<List<Profile>> ListProfilesWithoutGateAsync(string aid, CancellationToken ct)
    {
        var channel = await Transport.OpenLogicalChannelAsync(aid, ct).ConfigureAwait(false);
        try
        {
            return await new Sgp22Client(Transport, channel).GetProfilesInfoAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Transport.CloseLogicalChannelAsync(channel, cleanupCts.Token).ConfigureAwait(false);
        }
    }

    private sealed class EuiccForwardingProgress(Action<EuiccDownloadProgress> callback) : IProgress<EuiccDownloadProgress>
    {
        public void Report(EuiccDownloadProgress value) => callback(value);
    }

    private async Task<EuiccInfo> GetEuiccInfoWithoutGateAsync(string aid, CancellationToken ct)
    {
        var channel = await Transport.OpenLogicalChannelAsync(aid, ct).ConfigureAwait(false);
        try
        {
            return await new Sgp22Client(Transport, channel).GetEuiccInfoAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Transport.CloseLogicalChannelAsync(channel, cleanupCts.Token).ConfigureAwait(false);
        }
    }

    private static bool IsValidImei(string imei)
    {
        if (imei.Length != 15 || imei.Any(c => c is < '0' or > '9')) return false;
        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var digit = imei[i] - '0';
            if ((i & 1) == 1)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
        }
        var checkDigit = (10 - sum % 10) % 10;
        return imei[14] - '0' == checkDigit;
    }
}

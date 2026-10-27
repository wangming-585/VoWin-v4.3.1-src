using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using VoSharp.Common.Utils;
using VoSharp.Euicc.Models;
using VoSharp.Ike.Transport;
using VoSharp.Kernel;
using VoSharp.Kernel.Events;
using VoSharp.Kernel.Health;
using VoSharp.Kernel.Pool;
using VoSharp.Kernel.SipGateway;
using VoSharp.Modem;
using VoSharp.Modem.At;
using VoSharp.Sim;
using VoSharp.StateMachine;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.Sms;
using VoSharp.Telephony.VoWifi;
using VoWin.Helpers;
using VoWin.Models;

namespace VoWin.Services
{
    /// <summary>VoKernelService 拆分：eUICC 探测 / Profile 下载切换删除重命名 / EID 读取（partial，与主文件同一类型）。</summary>
    public partial class VoKernelService
    {
        public async Task<EuiccProbeResult> ProbeEuiccAsync(string? slotId = null, CancellationToken cancellationToken = default)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot == null)
            {
                return new EuiccProbeResult(
                    EuiccCapability.Error,
                    null,
                    Array.Empty<Profile>(),
                    "未发现可用卡槽，无法检测 eUICC 芯片。",
                    Array.Empty<string>());
            }

            var result = await slot.ProbeEuiccAsync(cancellationToken).ConfigureAwait(false);
            var level = result.Capability == EuiccCapability.Error ? "WARN" : "INFO";
            AddLog(level, "eSIM", $"卡槽 [{slot.Name}] eUICC 检测：{result.Message}");
            return result;
        }

        public async Task<IReadOnlyList<Profile>> GetEuiccProfilesAsync(string? slotId = null)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.GetEuiccProfilesAsync();
            }
            return await Kernel.GetEuiccProfilesAsync();
        }

        public async Task<bool> SwitchEuiccProfileAsync(string iccidOrAid, string? slotId = null, string? euiccAid = null, IProgress<ProfileSwitchProgress>? progress = null)
        {
            AddLog("INFO", "eSIM", $"Switching to eSIM profile: {iccidOrAid} on slot {slotId ?? "Active"}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                // Own the operation in the application service rather than the
                // current page. Navigation or a repeated click now observes the
                // same task and cannot abandon/duplicate a profile switch.
                var operation = _profileSwitchTasks.GetOrAdd(slot.Id,
                    _ => new Lazy<Task<bool>>(
                        () => RunProfileSwitchAsync(slot, iccidOrAid, euiccAid, progress),
                        LazyThreadSafetyMode.ExecutionAndPublication));
                return await operation.Value.ConfigureAwait(false);
            }
            return await Kernel.SwitchEuiccProfileAsync(iccidOrAid);
        }

        private async Task<bool> RunProfileSwitchAsync(ModemSlot slot, string iccidOrAid, string? euiccAid, IProgress<ProfileSwitchProgress>? progress = null)
        {
            try
            {
                // A queued auto-start belongs to the old SIM identity. Invalidate
                // it before the slot changes so it cannot wake up mid-switch and
                // contend for the modem AT channel.
                _voWifiDesiredStates[GetVoWifiPreferenceKey(slot)] = false;
                AdvanceVoWifiIntent(slot.Id);
                _autoVoWifiStarts.TryRemove(slot.Id, out _);

                var switched = await slot.SwitchEuiccProfileAsync(iccidOrAid, euiccAid: euiccAid, progress: progress).ConfigureAwait(false);
                if (switched)
                {
                    await ApplyPreferencesToSlotAsync(slot).ConfigureAwait(false);

                    // 切卡成功后无条件自动拉起 VoWiFi（用户明确要求的行为）。
                    // 上面 ApplyPreferencesToSlotAsync 是按「新卡」保存的 DefaultVoWifi
                    // 偏好走的：新卡从未开启过 VoWiFi 时偏好为 false，期望状态会被
                    // 写成 false，L3 看门狗也会误判为「用户手动关闭」而跳过拉起。
                    // 因此这里显式翻回开启、持久化到新卡，再排队自动启动。
                    _voWifiDesiredStates[GetVoWifiPreferenceKey(slot)] = true;
                    await PersistRuntimeIntentAsync(slot, voWifi: true).ConfigureAwait(false);
                    QueueAutoVoWifiStart(slot);
                    slot.VoWifi?.MarkRecentProfileSwitch();

                    // 切卡后 VoWiFi 快速重试：首次注册可能因 USIM 未就绪而失败，
                    // 延迟 3 秒检查，未注册则重试，最多 3 次（间隔 3s/5s/8s）。
                    SafeFire.Run(PostSwitchVoWifiFastRetryAsync(slot));

                    RequestSlotSync();

                    // 切卡会触发 SIM REFRESH：读卡器短暂 detach、逻辑通道失效。
                    // 这里不能立刻探测，必须等卡槽回到 Online 再读，
                    // 否则探测必失败、列表被清空。热键切卡也走这条路径。
                    SafeFire.Run(ReloadEuiccAfterSwitchAsync(slot));
                }
                return switched;
            }
            finally
            {
                _profileSwitchTasks.TryRemove(slot.Id, out _);
            }
        }

        /// <summary>
        /// 切卡后 VoWiFi 快速重试：首次注册可能因 USIM 未就绪而失败，
        /// 延迟 3 秒检查，未注册则重试，最多 3 次（间隔 3s/5s/8s）。
        /// </summary>
        private async Task PostSwitchVoWifiFastRetryAsync(ModemSlot slot)
        {
            try
            {
                // 初始延迟 5s：等卡片复位窗口（12-15s）的前半段过去，
                // 避免在复位最忙时启动 VoWiFi 导致身份刷新失败。
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                var retryDelays = new[]
                {
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(8),
                    TimeSpan.FromSeconds(12)
                };
                for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
                {
                    if (slot.VoWifi.State == VoWifiState.ImsRegistered)
                        return;

                    if (attempt > 0)
                        await Task.Delay(retryDelays[attempt - 1]).ConfigureAwait(false);

                    if (slot.VoWifi.State == VoWifiState.ImsRegistered)
                        return;

                    // 卡片状态预检：只有 SIM 身份已刷新、AKA 就绪、卡片可读时才启动 VoWiFi
                    if (!await IsSlotReadyForVoWifiAsync(slot, attempt).ConfigureAwait(false))
                    {
                        AddLog("WARN", "VoWiFi", $"切卡后 VoWiFi 快速重试第 {attempt + 1} 次跳过：卡片未就绪（{slot.Name}）；将继续重试");
                        continue;
                    }

                    var expectedIntentVersion = GetVoWifiIntentVersion(slot.Id);
                    AddLog("INFO", "VoWiFi", $"切卡后 VoWiFi 快速重试（第 {attempt + 1} 次）...");
                    // 单次失败不能终止整个重试循环：切卡复位期间常见
                    // "profile switch in progress"/"identity mismatch" 等瞬时错误，
                    // 必须等下一轮再试，否则只有第 1 次重试生效。
                    try
                    {
                        await StartVoWifiCoreAsync(slot, expectedIntentVersion).ConfigureAwait(false);
                        if (slot.VoWifi.State == VoWifiState.ImsRegistered)
                            return;
                    }
                    catch (Exception ex)
                    {
                        AddLog("WARN", "VoWiFi", $"切卡后 VoWiFi 快速重试第 {attempt + 1} 次未成功（{slot.Name}）：{ex.Message}；将继续重试");
                    }
                }
                AddLog("WARN", "VoWiFi", $"切卡后 VoWiFi 快速重试已用尽（{slot.Name}），交由看门狗接管");
            }
            catch (Exception ex)
            {
                AddLog("WARN", "VoWiFi", $"切卡后 VoWiFi 快速重试失败（{slot.Name}）：{ex.Message}");
            }
        }

        /// <summary>
        /// 卡片状态预检：检查 SIM 身份是否已刷新、AKA 是否就绪、卡片是否可读。
        /// 返回 true 表示卡片已就绪，可以启动 VoWiFi；返回 false 表示需要继续等待。
        /// </summary>
        private async Task<bool> IsSlotReadyForVoWifiAsync(ModemSlot slot, int attempt)
        {
            try
            {
                // PC/SC 读卡器：实时读取 EF.ICCID/EF.IMSI，不依赖缓存的 slot.Sim
                // 切卡后缓存的身份可能是旧卡数据，必须用实时读取的数据驱动判断
                if (slot.IsPcscReader && slot.Aka is VoSharp.Sim.PcscAkaProvider pcsc)
                {
                    try
                    {
                        // 1. 实时读取 EF.ICCID（卡片是否从复位恢复）
                        var liveIccid = await pcsc.ReadIccidAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(liveIccid))
                        {
                            AddLog("INFO", "SIM", $"Slot {slot.Name}: EF.ICCID 不可读（attempt {attempt + 1}），等待中...");
                            return false;
                        }

                        // 2. 用实时读取的 ICCID 调用 CheckReadyAsync（验证身份匹配）
                        if (!await pcsc.CheckReadyAsync(liveIccid).ConfigureAwait(false))
                        {
                            AddLog("INFO", "SIM", $"Slot {slot.Name}: AKA 未就绪（attempt {attempt + 1}），等待中...");
                            return false;
                        }

                        // 3. 实时读取 EF.IMSI（确认卡片完全就绪）
                        var liveImsi = await pcsc.ReadImsiAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(liveImsi))
                        {
                            AddLog("INFO", "SIM", $"Slot {slot.Name}: EF.IMSI 不可读（attempt {attempt + 1}），等待中...");
                            return false;
                        }

                        AddLog("INFO", "SIM", $"Slot {slot.Name}: 卡片预检通过（attempt {attempt + 1}），ICCID=***{liveIccid[^Math.Min(4, liveIccid.Length)..]}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        AddLog("INFO", "SIM", $"Slot {slot.Name}: 卡片预检失败（attempt {attempt + 1}）：{ex.Message}，等待中...");
                        return false;
                    }
                }

                // 非 PC/SC 读卡器：使用缓存的 slot.Sim（基带模组）
                if (slot.Sim == null || string.IsNullOrWhiteSpace(slot.Sim.Iccid))
                {
                    AddLog("INFO", "SIM", $"Slot {slot.Name}: SIM 身份未刷新（attempt {attempt + 1}），等待中...");
                    return false;
                }

                if (!await slot.Aka.CheckReadyAsync(slot.Sim.Iccid).ConfigureAwait(false))
                {
                    AddLog("INFO", "SIM", $"Slot {slot.Name}: AKA 未就绪（attempt {attempt + 1}），等待中...");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                AddLog("WARN", "SIM", $"Slot {slot.Name}: 卡片预检异常（attempt {attempt + 1}）：{ex.Message}");
                return false;
            }
        }

        public async Task<bool> DisableEuiccProfileAsync(string iccidOrAid, string? slotId = null, string? euiccAid = null)
        {
            AddLog("INFO", "eSIM", $"Disabling eSIM profile: {iccidOrAid} on slot {slotId ?? "Active"}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.DisableEuiccProfileAsync(iccidOrAid, euiccAid: euiccAid);
            }
            return await Kernel.DisableEuiccProfileAsync(iccidOrAid);
        }

        public async Task<bool> DeleteEuiccProfileAsync(string iccidOrAid, string? slotId = null, string? euiccAid = null)
        {
            AddLog("INFO", "eSIM", $"Deleting eSIM profile: {iccidOrAid}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.DeleteEuiccProfileAsync(iccidOrAid, euiccAid: euiccAid);
            }
            return await Kernel.DeleteEuiccProfileAsync(iccidOrAid);
        }

        public async Task<bool> RenameEuiccProfileAsync(string iccidOrAid, string nickname, string? slotId = null, string? euiccAid = null)
        {
            AddLog("INFO", "eSIM", $"Renaming profile {iccidOrAid} -> \"{nickname}\" on slot {slotId ?? "Active"}");
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.RenameEuiccProfileAsync(iccidOrAid, nickname, euiccAid: euiccAid);
            }
            return await Kernel.RenameEuiccProfileAsync(iccidOrAid, nickname);
        }

        public async Task<string> GetEuiccEidAsync(string? slotId = null)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (slot != null)
            {
                return await slot.GetEuiccEidAsync();
            }
            return await Kernel.GetEuiccEidAsync();
        }

        public async Task<EuiccDownloadResult> DownloadEuiccProfileAsync(
            string activationCode,
            string? confirmationCode = null,
            IProgress<EuiccDownloadProgress>? progress = null,
            string? slotId = null,
            CancellationToken cancellationToken = default,
            bool allowUntrustedTls = false,
            bool allowRetryAfterUncertain = false,
            string? euiccAid = null)
        {
            var slot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            AddLog("INFO", "eSIM", $"Starting verified eSIM profile download on slot {slot?.Name ?? "Active"}.");
            if (allowUntrustedTls)
                AddLog("WARN", "eSIM", "TLS certificate validation bypass enabled for this SM-DP+ download session.");
            if (allowRetryAfterUncertain)
                AddLog("WARN", "eSIM", "Provider-authorized retry enabled; the matching local uncertain transaction may be reset.");
            var tracedProgress = new CallbackProgress<EuiccDownloadProgress>(value =>
            {
                AddLog("INFO", "eSIM", $"Download progress {value.Percent}%: {value.Status}");
                progress?.Report(value);
            });
            try
            {
                var result = slot != null
                    ? await slot.DownloadEuiccProfileAsync(activationCode, confirmationCode, tracedProgress, cancellationToken, allowUntrustedTls, allowRetryAfterUncertain, euiccAid)
                    : await Kernel.DownloadEuiccProfileAsync(activationCode, confirmationCode, tracedProgress, cancellationToken, allowUntrustedTls, allowRetryAfterUncertain);
                AddLog(result.InstalledWithWarning ? "WARN" : "INFO", "eSIM",
                    $"Profile download completed. ICCID={result.Iccid}; Warning={result.Warning ?? "None"}");
                return result;
            }
            catch (Exception ex)
            {
                var errorChain = new List<string>();
                for (Exception? current = ex; current != null; current = current.InnerException)
                {
                    var message = current.Message.Trim();
                    if (!string.IsNullOrEmpty(message) && !errorChain.Contains(message, StringComparer.Ordinal))
                        errorChain.Add(message);
                }
                AddLog("ERROR", "eSIM", $"Profile download failed: {string.Join(" -> ", errorChain)}");
                throw;
            }
        }

    }
}

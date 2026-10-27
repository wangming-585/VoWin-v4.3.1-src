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
    /// <summary>VoKernelService 拆分：RTP 会话 / 麦克风采集 / 蜂窝音频桥接 / QDC507 语音路由 / PCM 媒体预留（partial，与主文件同一类型）。</summary>
    public partial class VoKernelService
    {
        private VoSharp.Telephony.Calls.RtpSession? GetActiveRtpSession()
        {
            if (Kernel is VoKernel k)
            {
                if (k.VoWifi?.ActiveRtpSession != null) return k.VoWifi.ActiveRtpSession;
                foreach (var slot in k.Pool.Slots.Values)
                {
                    if (slot.VoWifi?.ActiveRtpSession != null)
                        return slot.VoWifi.ActiveRtpSession;
                }
            }
            return null;
        }

        private void StartMicrophone()
        {
            try
            {
                if (_waveIn != null) StopMicrophone();
                _waveIn = new NAudio.Wave.WaveIn
                {
                    WaveFormat = new NAudio.Wave.WaveFormat(8000, 16, 1),
                    BufferMilliseconds = 20
                };
                _waveIn.DataAvailable += (s, a) =>
                {
                    var rtp = GetActiveRtpSession();
                    if (rtp != null && a.BytesRecorded > 0)
                    {
                        int sampleCount = a.BytesRecorded / 2;
                        short[] samples = new short[sampleCount];
                        Buffer.BlockCopy(a.Buffer, 0, samples, 0, a.BytesRecorded);
                        rtp.SendAudioPcm(samples);
                    }
                };
                _waveIn.StartRecording();
                AddLog("INFO", "Audio", "Microphone capture started for active call.");
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Audio", $"Failed to start microphone: {ex.Message}");
            }
        }

        private void StopMicrophone()
        {
            if (_waveIn != null)
            {
                try
                {
                    _waveIn.StopRecording();
                    _waveIn.Dispose();
                }
                catch { }
                _waveIn = null;
                AddLog("INFO", "Audio", "Microphone capture stopped.");
            }
        }

        public Task<CallExperienceSettings> GetCallExperienceSettingsAsync() => Task.FromResult(new CallExperienceSettings
        {
            VolteAudioPrewarmEnabled = _callExperience.VolteAudioPrewarmEnabled,
            SaveCallRecordings = _callExperience.SaveCallRecordings,
            RecordingDirectory = _callExperience.RecordingDirectory,
            AutoAnswerEnabled = _callExperience.AutoAnswerEnabled,
            AutoAnswerDelaySeconds = _callExperience.AutoAnswerDelaySeconds,
            AutoAnswerMessagePath = _callExperience.AutoAnswerMessagePath
        });

        public async Task SaveCallExperienceSettingsAsync(CallExperienceSettings settings)
        {
            settings.AutoAnswerDelaySeconds = Math.Clamp(settings.AutoAnswerDelaySeconds, 5, 120);
            _callExperience = settings;
            await _callExperienceStore.SaveAsync(settings);
            ApplyVolteAudioPrewarmPreference();
            ApplyCallRecordingPreference();
        }

        public SipGatewayStatus? GetSipGatewayStatus() => Kernel.LocalSipGateway?.Status;

        public async Task StartSipGatewayAsync(SipGatewayOptions options, CancellationToken cancellationToken = default)
        {
            await Kernel.StartSipGatewayAsync(options, cancellationToken).ConfigureAwait(false);
            var endpoint = Kernel.LocalSipGateway?.Status.LocalEndPoint;
            AddLog("INFO", "SipGateway", $"Private SIP bridge started on {endpoint}.");
        }

        public async Task StopSipGatewayAsync()
        {
            await Kernel.StopSipGatewayAsync().ConfigureAwait(false);
            AddLog("INFO", "SipGateway", "Private SIP bridge stopped.");
        }

        private void ApplyVolteAudioPrewarmPreference()
        {
            if (!_callExperience.VolteAudioPrewarmEnabled)
            {
                _callAlerting.ReleaseHostAudio();
                return;
            }

            if (CurrentCallState is CallState.Idle or CallState.Ended or CallState.Incoming)
                _callAlerting.PrewarmHostAudio();
        }

        private void ApplyCallRecordingPreference()
        {
            var directory = _callExperience.RecordingDirectory;
            Kernel.Calls.ConfigureAudioRecording(_callExperience.SaveCallRecordings, directory);
            foreach (var slot in Kernel.GetSlots())
                slot.Calls.ConfigureAudioRecording(_callExperience.SaveCallRecordings, directory);
        }

        private void StartIncomingAlertingAndAutoAnswer(string? slotId)
        {
            if (Volatile.Read(ref _incomingAutoAnswerCts) != null) return;
            ApplyVolteAudioPrewarmPreference();
            _callAlerting.StartRinging();
            var cts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref _incomingAutoAnswerCts, cts, null) != null) { cts.Dispose(); return; }
            if (!_callExperience.AutoAnswerEnabled) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_callExperience.AutoAnswerDelaySeconds), cts.Token);
                    if (cts.IsCancellationRequested || !HasIncomingCall || CurrentCallState != CallState.Incoming) return;
                    var message = _callExperience.AutoAnswerMessagePath;
                    _pendingAutoAnswerMessagePath = !string.IsNullOrWhiteSpace(message) && File.Exists(message) ? message : null;
                    AddLog("INFO", "Calls", "Incoming call timed out; answering automatically.");
                    await AnswerAsync(slotId);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AddLog("WARN", "Calls", $"Auto-answer failed ({ex.GetType().Name})."); }
            });
        }

        private void StopIncomingAlerting()
        {
            var cts = Interlocked.Exchange(ref _incomingAutoAnswerCts, null);
            cts?.Cancel();
            cts?.Dispose();
            _callAlerting.StopRinging();
        }

        private async Task EnsureDesktopMediaForCallAsync(CallConnectedEventArgs call)
        {
            if (Volatile.Read(ref _serviceDisposed) != 0) return;
            await _desktopAudioTransition.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _serviceDisposed) != 0) return;
                if (!string.Equals(Kernel.ActiveCall?.CallId, call.CallId, StringComparison.OrdinalIgnoreCase)) return;
                if (call.Codec?.StartsWith("Cellular", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (IsReserved(call.SlotId))
                    {
                        AddLog("INFO", "CellularAudio", "Cellular UAC is reserved for the SIP/RTP bridge; desktop microphone/speaker startup was skipped.");
                        return;
                    }
                    if (!TryClaimDesktopAudio(call.CallId, call.SlotId, DesktopAudioKind.CellularUsb)) return;
                    _callAlerting.ReleaseHostAudio();
                    var audioCts = new CancellationTokenSource();
                    var previous = Interlocked.Exchange(ref _cellularCallAudioCts, audioCts);
                    previous?.Cancel();
                    previous?.Dispose();
                    var keepAudio = false;
                    try
                    {
                        var started = await StartCellularCallAudioAsync(
                            call.Codec.Contains("Cellular/UAC", StringComparison.OrdinalIgnoreCase),
                            Interlocked.Exchange(ref _pendingAutoAnswerMessagePath, null), audioCts.Token).ConfigureAwait(false);
                        keepAudio = started && string.Equals(Kernel.ActiveCall?.CallId, call.CallId, StringComparison.OrdinalIgnoreCase);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        AddLog("ERROR", "CellularAudio", $"Unexpected cellular audio failure: {ex.Message}");
                    }
                    finally
                    {
                        if (!keepAudio)
                        {
                            ReleaseDesktopAudioClaim(call.CallId, call.SlotId);
                            if (ReferenceEquals(Interlocked.CompareExchange(ref _cellularCallAudioCts, null, audioCts), audioCts))
                            {
                                try { audioCts.Cancel(); } catch (ObjectDisposedException) { }
                                audioCts.Dispose();
                            }
                            await StopCellularAudioBridgeAsync().ConfigureAwait(false);
                            await StopQdc507VoiceRouteAsync().ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    var slot = !string.IsNullOrWhiteSpace(call.SlotId)
                        ? Slots.FirstOrDefault(candidate => candidate.Id.Equals(call.SlotId, StringComparison.OrdinalIgnoreCase))
                        : null;
                    if (slot?.Calls.ExternalMediaBridgeEnabled == true) return;
                    if (!TryClaimDesktopAudio(call.CallId, call.SlotId, DesktopAudioKind.Microphone)) return;
                    PostToUi(() =>
                    {
                        if (!string.Equals(Kernel.ActiveCall?.CallId, call.CallId, StringComparison.OrdinalIgnoreCase))
                        {
                            ReleaseDesktopAudioClaim(call.CallId, call.SlotId);
                            return;
                        }
                        _callAlerting.ReleaseHostAudio();
                        StartMicrophone();
                    });
                }
            }
            finally
            {
                _desktopAudioTransition.Release();
            }
        }

        private async Task StopDesktopCellularAudioAsync()
        {
            await _desktopAudioTransition.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCellularAudioBridgeAsync().ConfigureAwait(false);
                await StopQdc507VoiceRouteAsync().ConfigureAwait(false);
            }
            finally
            {
                _desktopAudioTransition.Release();
            }
        }

        private async Task ResumePromotedCallAudioAsync(Task cleanup, CallConnectedEventArgs promoted)
        {
            try
            {
                await cleanup.ConfigureAwait(false);
                await EnsureDesktopMediaForCallAsync(promoted).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AddLog("WARN", "CellularAudio", $"Could not resume audio for the remaining call: {ex.Message}");
            }
        }

        private async Task<bool> StartCellularAudioBridgeAsync(
            string? messagePath,
            CancellationToken cancellationToken,
            bool externalPcm = false)
        {
            try
            {
                await _cellularAudioBridge.StartAsync(messagePath, cancellationToken, externalPcm);
                AddLog("INFO", "CellularAudio", externalPcm
                    ? "SIP RTP PCM connected directly to modem USB Audio Class endpoints."
                    : "PC microphone/speaker connected to modem USB Audio Class endpoints.");
                return true;
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "CellularAudio", $"Failed to start cellular audio bridge: {ex.Message}");
                return false;
            }
        }

        private async Task StopCellularAudioBridgeAsync()
        {
            try
            {
                var wasRunning = _cellularAudioBridge.IsRunning;
                await _cellularAudioBridge.StopAsync();
                if (wasRunning)
                    AddLog("INFO", "CellularAudio", "Cellular USB audio bridge stopped.");
            }
            catch (Exception ex)
            {
                AddLog("WARN", "CellularAudio", $"Failed to stop cellular audio bridge: {ex.Message}");
            }
        }

        private async Task PrepareQdc507VoiceRuntimeAsync()
        {
            try
            {
                await _qdc507VoiceRuntime.PrepareAsync();
                AddLog("INFO", "CellularAudio", "QDC507 module voice drivers and VoLTE calibration are ready.");
            }
            catch (Exception ex)
            {
                AddLog("INFO", "CellularAudio", $"QDC507 runtime is not available: {ex.Message}");
            }
        }

        private async Task<bool> StartCellularCallAudioAsync(
            bool standardUacReady,
            string? messagePath,
            CancellationToken cancellationToken,
            bool externalPcm = false)
        {
            PostToUi(StopMicrophone);
            if (!standardUacReady)
            {
                try
                {
                    AddLog("INFO", "CellularAudio", "Starting QDC507 D4/UAC VoLTE route...");
                    Interlocked.Exchange(ref _qdc507VoiceRouteActive, 1);
                    await _qdc507VoiceRuntime.StartRouteAsync(cancellationToken);
                    AddLog("INFO", "CellularAudio", "QDC507 D4/UAC VoLTE route is RUNNING.");
                    // QDC507 briefly republishes its UAC stream after audio_enable=1.
                    // Opening the old endpoint immediately succeeds but remains a zero stream.
                    // On QDC507 the D4 endpoint accepts an early open but then
                    // remains a zero stream for the whole call. The module needs
                    // roughly six seconds after audio_enable before MME opens it.
                    await Task.Delay(TimeSpan.FromSeconds(7), cancellationToken);
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception ex)
                {
                    await StopQdc507VoiceRouteAsync().ConfigureAwait(false);
                    AddLog("ERROR", "CellularAudio",
                        $"The modem rejected AT+QPCMV and the QDC507 runtime route could not start: {ex.Message}");
                    CallMediaStatusChanged?.Invoke("Cellular/Audio unavailable");
                    return false;
                }
            }

            // audio_enable=1 can briefly re-enumerate the Windows UAC endpoints.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var started = await StartCellularAudioBridgeAsync(messagePath, cancellationToken, externalPcm);
                if (started)
                {
                    AddLog("INFO", "CellularAudio", $"Audio devices: {_cellularAudioBridge.DeviceSummary}");
                    CallMediaStatusChanged?.Invoke(standardUacReady
                        ? "Cellular/UAC 8 kHz"
                        : "Cellular/QDC507 UAC 8 kHz");
                    SafeFire.Run(MonitorCellularAudioLevelsAsync(cancellationToken, externalPcm));
                    return true;
                }
                await Task.Delay(250, cancellationToken);
            }
            AddLog("ERROR", "CellularAudio", "QDC507 voice route is active, but Windows UAC endpoints did not become ready.");
            CallMediaStatusChanged?.Invoke("Cellular/Windows audio unavailable");
            return false;
        }

        private async Task MonitorCellularAudioLevelsAsync(CancellationToken cancellationToken, bool externalPcm = false)
        {
            var reopenedZeroStream = false;
            try
            {
                for (var sample = 0; sample < 4; sample++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    var levels = _cellularAudioBridge.ReadAndResetPeaks();
                    AddLog("INFO", "CellularAudio",
                        externalPcm
                            ? $"Live PCM peaks: SIP RTP -> modem {levels.HostMicrophonePeak}, modem -> SIP RTP {levels.ModemDownlinkPeak}."
                            : $"Live PCM peaks: PC microphone -> modem {levels.HostMicrophonePeak}, modem -> PC speaker {levels.ModemDownlinkPeak}.");

                    if (!reopenedZeroStream && sample == 0 && levels.ModemDownlinkPeak <= 1)
                    {
                        reopenedZeroStream = true;
                        AddLog("WARN", "CellularAudio", "Modem UAC is still a zero stream; reopening endpoints after enumeration settled.");
                        await StartCellularAudioBridgeAsync(null, cancellationToken, externalPcm);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task StopQdc507VoiceRouteAsync()
        {
            if (Interlocked.Exchange(ref _qdc507VoiceRouteActive, 0) == 0) return;
            try { await _qdc507VoiceRuntime.StopRouteAsync(); }
            catch (Exception ex) { AddLog("WARN", "CellularAudio", $"Failed to stop QDC507 voice route: {ex.Message}"); }
        }

        public void Reserve(string slotId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(slotId);
            lock (_cellularSipReservationGate)
            {
                // Windows UAC selection and the PCM bridge are process-wide.
                // Refuse a second carrier leg until each modem has its own
                // independently mapped audio endpoint.
                if (!_cellularSipReservations.IsEmpty)
                    throw new InvalidOperationException("Cellular USB audio is already reserved by another SIP call.");
                if (_cellularAudioBridge.IsRunning || _desktopAudioKind == DesktopAudioKind.CellularUsb)
                    throw new InvalidOperationException("Cellular USB audio is already in use by a local call.");
                _cellularSipReservations[slotId] = 0;
            }
        }

        public bool IsReserved(string? slotId) =>
            !string.IsNullOrWhiteSpace(slotId) && _cellularSipReservations.ContainsKey(slotId);

        public async Task<ICallPcmMedia> OpenAsync(string slotId, CancellationToken ct)
        {
            var slot = Slots.FirstOrDefault(candidate => candidate.Id.Equals(slotId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Cellular SIP media slot '{slotId}' is unavailable.");
            if (!IsReserved(slotId)) throw new InvalidOperationException("Cellular UAC was not reserved for SIP media.");

            var audioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var previous = Interlocked.Exchange(ref _cellularCallAudioCts, audioCts);
            previous?.Cancel();
            previous?.Dispose();
            var started = await StartCellularCallAudioAsync(
                slot.CellularUsbAudioAvailable,
                messagePath: null,
                audioCts.Token,
                externalPcm: true).ConfigureAwait(false);
            if (!started) throw new InvalidOperationException("The modem USB Audio PCM endpoint did not become ready for SIP.");
            return _cellularAudioBridge;
        }

        public async Task ReleaseAsync(string? slotId)
        {
            if (string.IsNullOrWhiteSpace(slotId)) return;
            if (!_cellularSipReservations.TryRemove(slotId, out _)) return;
            var audioCts = Interlocked.Exchange(ref _cellularCallAudioCts, null);
            audioCts?.Cancel();
            audioCts?.Dispose();
            await StopCellularAudioBridgeAsync().ConfigureAwait(false);
            await StopQdc507VoiceRouteAsync().ConfigureAwait(false);
        }
    }
}

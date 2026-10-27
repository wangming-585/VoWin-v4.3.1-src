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
    public partial class VoKernelService : IVoKernelService, INotifyPropertyChanged, ICellularSipMediaProvider
    {
    private DiagnosticMonitor? Diag => (Application.Current as App)?._diagnosticMonitor;

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _isInitialScanRunning = true;
        public bool IsInitialScanRunning
        {
            get => _isInitialScanRunning;
            private set
            {
                if (_isInitialScanRunning == value) return;
                _isInitialScanRunning = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInitialScanRunning)));
            }
        }

        private string _initialScanStatus = "正在准备设备扫描…";
        public string InitialScanStatus
        {
            get => _initialScanStatus;
            private set
            {
                if (_initialScanStatus == value) return;
                _initialScanStatus = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InitialScanStatus)));
            }
        }

        private readonly ConcurrentDictionary<string, byte> _autoVoWifiStarts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> _voWifiIntentVersions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _voWifiDesiredStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _profileSwitchTasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _runtimePreferenceGate = new(1, 1);
        public IVoKernel Kernel { get; }

        public ObservableCollection<ModemSlot> Slots { get; } = new();
        public ModemSlot? ActiveSlot => Kernel.GetActiveSlot();
        public ObservableCollection<SmsConversationModel> Conversations { get; } = new();
        public ObservableCollection<CallRecordModel> CallHistory { get; } = new();
        public ObservableCollection<ProxyNodeModel> ProxyPresets { get; } = new();
        public ObservableCollection<IccidRouteModel> IccidRoutes { get; } = new();
        public ObservableCollection<CountryRouteModel> CountryRoutes { get; } = new();
        public ObservableCollection<LogEntryModel> Logs { get; } = new();

        public TelephonyState StateMachineState => Kernel.StateMachine.CurrentState;
        public SignalQuality? CurrentSignal => Kernel.Pool.ActiveSlot?.Signal;
        public NetworkRegistration? CurrentRegistration => Kernel.Pool.ActiveSlot?.Registration;
        public SimIdentity? CurrentSim => Kernel.CurrentSim;
        public VoWifiDiagnosticInfo? VoWifiDiag
        {
            get
            {
                try
                {
                    return Kernel.GetVoWifiDiagnostics();
                }
                catch (Exception ex)
                {
                    AddLog("WARN", "VoWiFi", $"Failed to read VoWiFi diagnostics: {ex.Message}");
                    return null;
                }
            }
        }
        public CallSession? ActiveCall => Kernel.ActiveCall;
        public CallState CurrentCallState { get; private set; } = CallState.Idle;
        public string? CurrentCallNumber { get; private set; }
        public bool HasIncomingCall { get; private set; }
        public string? IncomingCallerNumber { get; private set; }
        public bool IsPresentedCallEvent(string callId) =>
            !string.IsNullOrWhiteSpace(callId) &&
            string.Equals(_presentedCallId, callId, StringComparison.OrdinalIgnoreCase);
        public event Action<string>? CallMediaStatusChanged;
        public event Action<SmsMessageModel>? IncomingSmsReceived;
        public event Action<string, string?>? IncomingCallReceived;
        public event Action? EgressRoutesChanged;

        /// <summary>eUICC 探测完成（槽位 ID, 结果）。切卡等后台流程结束后用它通知界面刷新。</summary>
        public event Action<string, EuiccProbeResult>? EuiccProbeCompleted;

        public IPreferenceDatabaseService Preferences { get; }
        private readonly Dispatcher _dispatcher;
        private CancellationTokenSource? _usbDebounceCts;
        private readonly ConcurrentQueue<LogEntryModel> _pendingUiLogs = new();
        private readonly Channel<string> _logLines = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        private readonly CancellationTokenSource _logWriterCts = new();
        private readonly Task _logWriterTask;
        private readonly SemaphoreSlim _slotDiscoveryGate = new(1, 1);

        // Three-tier watchdog hierarchy (L1 slot 5s / L2 pool 15s / L3 global 30s).
        private WatchdogHealthRegistry? _watchdogRegistry;
        private SlotWatchdog? _slotWatchdog;
        private PoolWatchdog? _poolWatchdog;
        private GlobalWatchdog? _globalWatchdog;
        private int _logDrainScheduled;
        private int _slotSyncScheduled;
        private int _slotSyncRequestVersion;

        // Microphone capture
        private NAudio.Wave.WaveIn? _waveIn;
        private readonly CellularAudioBridge _cellularAudioBridge = new();
        private readonly Qdc507VoiceRuntime _qdc507VoiceRuntime = new();
        private int _qdc507VoiceRouteActive;
        private readonly CallAlerting _callAlerting = new();
        private readonly CallExperienceSettingsStore _callExperienceStore = new();
        private CancellationTokenSource? _cellularCallAudioCts;
        private string? _desktopAudioCallId;
        private string? _desktopAudioSlotId;
        private enum DesktopAudioKind { None, Microphone, CellularUsb }
        private DesktopAudioKind _desktopAudioKind;
        private string? _presentedCallId;
        private string? _shownIncomingCallId;
        private readonly ConcurrentDictionary<string, (string Number, DateTime Started, CallDirection Direction)> _callEventMetadata = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CallConnectedEventArgs> _connectedCallEvents = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _recordedCallEvents = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _desktopAudioTransition = new(1, 1);
        private CancellationTokenSource? _incomingAutoAnswerCts;
        private CallExperienceSettings _callExperience = new();
        private string? _pendingAutoAnswerMessagePath;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _syncingSlots = new();
        private readonly ConcurrentDictionary<string, byte> _cellularSipReservations = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _cellularSipReservationGate = new();

        private static string CallEventKey(string? slotId, string callId) => $"{slotId ?? "fallback"}\0{callId}";

        private void PresentIncomingCall(string callId, string callerNumber, string? slotId)
        {
            _presentedCallId = callId;
            HasIncomingCall = true;
            IncomingCallerNumber = callerNumber;
            CurrentCallNumber = callerNumber;
            CurrentCallState = CallState.Incoming;
            if (string.Equals(_shownIncomingCallId, callId, StringComparison.OrdinalIgnoreCase)) return;
            _shownIncomingCallId = callId;

            var slotName = !string.IsNullOrEmpty(slotId)
                ? Slots.FirstOrDefault(slot => slot.Id.Equals(slotId, StringComparison.OrdinalIgnoreCase))?.Name
                : null;
            Views.Windows.IncomingCallFloatingWindow.ShowIncoming(
                callerNumber,
                slotName ?? ActiveSlot?.Name ?? "SIM",
                onAnswer: async () =>
                {
                    await AnswerAsync(slotId);
                    TrayManager.RestoreMainWindow();
                },
                onReject: async () => { await RejectAsync(slotId); });
            StartIncomingAlertingAndAutoAnswer(slotId);
        }

        private bool TryClaimDesktopAudio(string callId, string? slotId, DesktopAudioKind kind)
        {
            lock (_cellularSipReservationGate)
            {
                if (kind == DesktopAudioKind.CellularUsb && !_cellularSipReservations.IsEmpty)
                    return false;
                if (_desktopAudioCallId != null)
                    return false;
                _desktopAudioCallId = callId;
                _desktopAudioSlotId = slotId;
                _desktopAudioKind = kind;
                return true;
            }
        }

        private DesktopAudioKind ReleaseDesktopAudioClaim(string callId, string? slotId)
        {
            lock (_cellularSipReservationGate)
            {
                if (!string.Equals(_desktopAudioCallId, callId, StringComparison.OrdinalIgnoreCase) ||
                    (_desktopAudioSlotId != null && slotId != null &&
                     !string.Equals(_desktopAudioSlotId, slotId, StringComparison.OrdinalIgnoreCase)))
                    return DesktopAudioKind.None;
                var kind = _desktopAudioKind;
                _desktopAudioCallId = null;
                _desktopAudioSlotId = null;
                _desktopAudioKind = DesktopAudioKind.None;
                return kind;
            }
        }

        private void ReconcileRemovedCallSlot(string slotId)
        {
            Task audioCleanup = Task.CompletedTask;
            string? audioCallId;
            lock (_cellularSipReservationGate)
                audioCallId = string.Equals(_desktopAudioSlotId, slotId, StringComparison.OrdinalIgnoreCase)
                    ? _desktopAudioCallId : null;
            if (audioCallId != null)
            {
                var kind = ReleaseDesktopAudioClaim(audioCallId, slotId);
                if (kind == DesktopAudioKind.CellularUsb)
                {
                    var audioCts = Interlocked.Exchange(ref _cellularCallAudioCts, null);
                    audioCts?.Cancel();
                    audioCts?.Dispose();
                    audioCleanup = StopDesktopCellularAudioAsync();
                }
                else if (kind == DesktopAudioKind.Microphone)
                    StopMicrophone();
            }
            if (IsReserved(slotId)) audioCleanup = Task.WhenAll(audioCleanup, ReleaseAsync(slotId));

            foreach (var entry in _callEventMetadata.Where(item =>
                         item.Key.StartsWith(slotId + "\0", StringComparison.OrdinalIgnoreCase)))
            {
                if (!_callEventMetadata.TryRemove(entry.Key, out var call)) continue;
                _connectedCallEvents.TryRemove(entry.Key, out _);
                if (_recordedCallEvents.TryAdd(entry.Key, DateTime.UtcNow))
                    AddCallRecord(call.Number, call.Direction, CallState.Ended,
                        call.Started, DateTime.UtcNow - call.Started, slotId: slotId);
            }

            var next = Kernel.ActiveCall;
            if (_presentedCallId == null || string.Equals(_presentedCallId, next?.CallId, StringComparison.OrdinalIgnoreCase)) return;
            StopIncomingAlerting();
            _callAlerting.StopRingback();
            if (next == null)
            {
                CurrentCallState = CallState.Ended;
                HasIncomingCall = false;
                _shownIncomingCallId = null;
                Views.Windows.IncomingCallFloatingWindow.Dismiss();
                Views.Windows.InCallFloatingWindow.Dismiss();
                return;
            }
            if (next.State == CallState.Incoming)
                PresentIncomingCall(next.CallId, next.RemoteNumber, Kernel.ActiveCallSlotId);
            else
            {
                _presentedCallId = next.CallId;
                CurrentCallState = next.State;
                CurrentCallNumber = next.RemoteNumber;
                HasIncomingCall = false;
                _shownIncomingCallId = null;
                Views.Windows.IncomingCallFloatingWindow.Dismiss();
            }
            if (next.State == CallState.Active &&
                _connectedCallEvents.TryGetValue(CallEventKey(Kernel.ActiveCallSlotId, next.CallId), out var connected))
                SafeFire.Run(ResumePromotedCallAudioAsync(audioCleanup, connected));
        }

        public VoKernelService(IPreferenceDatabaseService? preferences = null)
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            Preferences = preferences ?? new PreferenceDatabaseService();
            Kernel = new VoKernel();
            Kernel.CellularSipMediaProvider = this;
            _logWriterTask = RunLogWriterAsync(_logWriterCts.Token);

            RegisterKernelEvents();
        }

        private static readonly HashSet<string> LegacyBundledCountryCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "CN", "US", "GB", "HK", "JP", "SG"
        };

        /// <summary>
        /// Removes only the built-in examples that older releases wrote to the
        /// database.  User-created entries are intentionally retained.  A
        /// profile which pointed at one of the examples is removed as well so a
        /// no-config installation cannot silently regain a localhost proxy.
        /// </summary>
        private async Task RemoveLegacyBundledEgressDefaultsAsync(
            List<ProxyNodeModel> savedNodes,
            List<CountryRouteModel> savedRoutes)
        {
            var bundledNodes = savedNodes.Where(IsLegacyBundledProxy).ToList();
            var bundledNodeIds = bundledNodes
                .Select(node => node.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var bundledRoutes = savedRoutes
                .Where(route =>
                    (route.IsDirect && LegacyBundledCountryCodes.Contains(route.CountryCode)) ||
                    (!string.IsNullOrWhiteSpace(route.ProxyNodeId) && bundledNodeIds.Contains(route.ProxyNodeId)))
                .ToList();

            foreach (var route in bundledRoutes)
            {
                await Preferences.DeleteCountryRouteAsync(route.CountryCode);
                savedRoutes.Remove(route);
            }

            foreach (var node in bundledNodes)
            {
                await Preferences.DeleteProxyNodeAsync(node.Id);
                savedNodes.Remove(node);
            }

            if (bundledNodes.Count != 0 || bundledRoutes.Count != 0)
            {
                AddLog("INFO", "Proxy",
                    $"已清理旧版本内置代理/分流项：{bundledNodes.Count} 个代理节点，{bundledRoutes.Count} 条路由。未配置时将保持直连。");
            }
        }

        private static bool IsLegacyBundledProxy(ProxyNodeModel node)
        {
            if (!string.IsNullOrWhiteSpace(node.Username) || !string.IsNullOrWhiteSpace(node.Password) ||
                !node.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return (node.Name.Equals("本地 Clash / v2ray (SOCKS5)", StringComparison.Ordinal) &&
                    node.Protocol.Equals("socks5", StringComparison.OrdinalIgnoreCase) && node.Port == 7890) ||
                   (node.Name.Equals("通用本地 SOCKS5 (1080)", StringComparison.Ordinal) &&
                    node.Protocol.Equals("socks5", StringComparison.OrdinalIgnoreCase) && node.Port == 1080) ||
                   (node.Name.Equals("本地 HTTP 代理 (7890)", StringComparison.Ordinal) &&
                    node.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase) && node.Port == 7890);
        }

        public async Task InitializeAsync()
        {
        Diag?.LogCheckpoint("VoKernelService.InitializeAsync 开始");

            IsInitialScanRunning = true;
            InitialScanStatus = "正在加载本地设置…";
            AddLog("INFO", "VoKernelService", "Initializing VoSharp core engine & SQLite database...");

            try
            {
                await Preferences.InitializeAsync();
                _callExperience = await _callExperienceStore.LoadAsync();
                ApplyVolteAudioPrewarmPreference();
                ApplyCallRecordingPreference();

                Diag?.LogCheckpoint("InitializeAsync 数据加载开始");

                // 1. Load Call Records from SQLite —— 与短信、代理路由并行加载
                var callHistoryTask = Preferences.GetCallRecordsAsync(300);
                var allSmsTask = Preferences.GetRecentSmsMessagesAsync(500);
                var savedNodesTask = Preferences.GetAllProxyNodesAsync();
                var savedRoutesTask = Preferences.GetAllCountryRoutesAsync();
                var savedIccidRoutesTask = Preferences.GetAllIccidRoutesAsync();
                var legacySimPreferencesTask = Preferences.GetAllSimPreferencesAsync();
                var callHistory = await callHistoryTask;
                await RunOnUiAsync(() =>
                {
                    CallHistory.Clear();
                    foreach (var call in callHistory)
                    {
                        CallHistory.Add(call);
                    }
                });

                // 2. Load SMS Messages & Reconstruct Conversations（并行任务已启动）
                var allSms = await allSmsTask;
                await RunOnUiAsync(() =>
                {
                    Conversations.Clear();
                    var grouped = allSms.GroupBy(m => m.SenderOrRecipient.Trim(), StringComparer.OrdinalIgnoreCase);
                    string[] colors = { "#0078D4", "#107C10", "#D13438", "#8764B8", "#FF8C00", "#008272", "#E3008C" };
                    var convList = new List<SmsConversationModel>();

                    foreach (var g in grouped)
                    {
                        var clean = g.Key;
                        int hash = Math.Abs(clean.GetHashCode());
                        var letter = clean.Length > 0 ? (char.IsLetter(clean[0]) ? clean[0].ToString().ToUpper() : clean[^1].ToString()) : "#";
                        var conv = new SmsConversationModel
                        {
                            ContactNumber = clean,
                            AvatarLetter = letter,
                            AvatarColor = colors[hash % colors.Length]
                        };
                        foreach (var m in g.OrderBy(x => x.Timestamp))
                        {
                            conv.Messages.Add(m);
                            if (!m.IsOutgoing && m.DeliveryState == SmsDeliveryState.Received)
                            {
                                conv.UnreadCount++;
                            }
                        }
                        conv.UpdateLastMessage();
                        convList.Add(conv);
                    }

                    foreach (var conv in convList.OrderByDescending(c => c.LastMessageTime))
                    {
                        Conversations.Add(conv);
                    }
                });

                // 3. Load only user-created proxy nodes and country routes.  No
                // local proxy or country route is assumed by default: an empty
                // configuration must remain direct and never inherit a TUN's
                // incidental localhost listener.
                var savedNodes = (await savedNodesTask).ToList();
                var savedRoutes = (await savedRoutesTask).ToList();
                var savedIccidRoutes = (await savedIccidRoutesTask).ToList();
                await RemoveLegacyBundledEgressDefaultsAsync(savedNodes, savedRoutes);

                // Older releases stored a per-SIM proxy URL inside SimPreferences.
                // Promote it once into the explicit ICCID routing tier so existing
                // users keep their route and can now see/edit the rule in one place.
                var legacySimPreferences = await legacySimPreferencesTask;
                foreach (var simPreference in legacySimPreferences.Where(preference =>
                             !string.IsNullOrWhiteSpace(preference.Iccid) &&
                             !string.IsNullOrWhiteSpace(preference.DedicatedProxyUrl)))
                {
                    if (savedIccidRoutes.Any(route => string.Equals(route.Iccid, simPreference.Iccid, StringComparison.Ordinal)))
                        continue;

                    var legacyUrl = simPreference.DedicatedProxyUrl!.Trim();
                    var matchingNode = savedNodes.FirstOrDefault(node =>
                        string.Equals(node.ToProxyUrl(), legacyUrl, StringComparison.OrdinalIgnoreCase));
                    var migrated = new IccidRouteModel
                    {
                        Iccid = simPreference.Iccid.Trim(),
                        CardName = simPreference.CardNickname,
                        ImsiSnapshot = simPreference.Imsi,
                        PhoneNumber = null,
                        ProxyNodeId = matchingNode?.Id,
                        ProxyUrl = matchingNode == null ? legacyUrl : null,
                        ProxyNodeName = matchingNode?.Name ?? DescribeProxyEndpoint(legacyUrl),
                        UpdatedAt = DateTime.Now
                    };
                    savedIccidRoutes.Add(migrated);
                    await Preferences.SaveIccidRouteAsync(migrated);
                }

                if (savedNodes.Count > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        ProxyPresets.Clear();
                        foreach (var n in savedNodes) ProxyPresets.Add(n);
                    });
                }

                if (savedRoutes.Count > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        CountryRoutes.Clear();
                        foreach (var r in savedRoutes) CountryRoutes.Add(r);
                    });
                }

                if (savedIccidRoutes.Count > 0)
                {
                    await RunOnUiAsync(() =>
                    {
                        IccidRoutes.Clear();
                        foreach (var route in savedIccidRoutes) IccidRoutes.Add(route);
                    });
                }

                AddLog("INFO", "Preferences", $"SQLite initialized ({callHistory.Count} calls, {allSms.Count} messages loaded)");
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Preferences", $"SQLite initialization error: {ex.Message}");
            }

            await RunOnUiAsync(SyncSlots);

            Diag?.LogCheckpoint("InitializeAsync 数据加载完成");
            AddLog("INFO", "VoKernelService", "Starting initial device & SIM scan...");
            InitialScanStatus = "正在识别设备类型…";
            var hasPcscReader = false;
            try
            {
                // Device-type-first: a PC/SC smart-card reader indicates a card
                // reader setup — read the card immediately and defer serial-port
                // (cellular module) probing so boot never blocks card identity
                // behind base-station scans.
                hasPcscReader = ModemPool.ListPcscReaders().Count > 0;

                if (hasPcscReader)
                {
                    // Smart-card reader present → read the card first.
                    InitialScanStatus = "正在读取智能卡…";
                    var pcscFound = await DiscoverPcscSlotsCoreAsync();
                    await RunOnUiAsync(SyncSlots);
                    AddLog("INFO", "VoKernelService", $"Smart-card reader detected, attached {pcscFound.Count} slot(s)");
                    await ApplyProbeAndPreferencesAsync(pcscFound);

                    // Deferred, non-blocking serial-modem (cellular module) scan.
                    InitialScanStatus = "正在后台扫描串口设备…";
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var serialFound = await DiscoverSerialSlotsCoreAsync();
                            await RunOnUiAsync(SyncSlots);
                            AddLog("INFO", "VoKernelService", $"Background serial scan finished, found {serialFound.Count} slot(s)");
                            await ApplyProbeAndPreferencesAsync(serialFound);
                            Diag?.LogCheckpoint("InitializeAsync 槽位恢复完成");
                            if (Slots.Count == 0)
                                AddLog("INFO", "VoKernelService", "No Modem found");
                        }
                        catch (Exception ex)
                        {
                            AddLog("WARN", "VoKernelService", $"Background serial scan failed: {ex.Message}");
                        }
                        finally
                        {
                            await RunOnUiAsync(() =>
                            {
                                InitialScanStatus = "扫描完成";
                                IsInitialScanRunning = false;
                            });
                        }
                    });
                }
                else
                {
                    // No smart-card reader → cellular-module only path.
                    InitialScanStatus = "正在并发扫描串口设备…";
                    var ports = SerialPort.GetPortNames();
                    AddLog("INFO", "VoKernelService", $"Ports: [{(ports.Length > 0 ? string.Join(", ", ports) : "None")}]");

                    var found = await DiscoverSerialSlotsCoreAsync();
                    await RunOnUiAsync(SyncSlots);
                    AddLog("INFO", "VoKernelService", $"Scan finished, found {found.Count} slots");
                    await ApplyProbeAndPreferencesAsync(found);
                    Diag?.LogCheckpoint("InitializeAsync 槽位恢复完成");
                    if (Slots.Count == 0)
                        AddLog("INFO", "VoKernelService", "No Modem found");
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", "VoKernelService", $"Scan failed: {ex.Message}");
            }
            finally
            {
                // When a background serial task is running (PC/SC branch) its own
                // finally finalizes status/scan-flag on completion.
                if (!hasPcscReader)
                {
                    InitialScanStatus = "扫描完成";
                    IsInitialScanRunning = false;
                }
            }

            StartWatchdogs();

            StartWatchdogs();
        }

        /// <summary>Starts L1→L2→L3; stopped in reverse order on release.</summary>
        private void StartWatchdogs()
        {
        Diag?.LogCheckpoint("StartWatchdogs 开始");

            if (_slotWatchdog != null) return;

            _watchdogRegistry = new WatchdogHealthRegistry(Kernel.EventBus);
            // 健康快照必须回写到卡槽，否则 UI 上的健康徽章永远停留在"未评估"：
            // registry 只对外发布事件，卡槽自身的 WatchdogHealth 不会自动更新。
            _watchdogRegistry.HealthChanged += OnWatchdogHealthChanged;
            _slotWatchdog = new SlotWatchdog(Kernel.Pool, _watchdogRegistry, Kernel.EventBus);
            _poolWatchdog = new PoolWatchdog(Kernel.Pool, _watchdogRegistry, Kernel.EventBus);
            _globalWatchdog = new GlobalWatchdog(this, _watchdogRegistry);

            _slotWatchdog.Start();
            _poolWatchdog.Start();
            _globalWatchdog.Start();
            AddLog("INFO", "Watchdog", "三级看门狗已启动：L1 卡槽 5s / L2 设备池 15s / L3 全局 30s；L3 已接管 VoWiFi 恢复决策，SIM 就绪即自动拉起。");
        }

        /// <summary>
        /// 把 L1/L2/L3 发布的健康快照镜像到对应卡槽，供设备卡片绑定显示。
        /// registry 在后台工作线程上发布，必须切回 UI 线程再改可绑定属性。
        /// </summary>
        private void OnWatchdogHealthChanged(object? sender, SlotHealth health)
        {
            if (health == null) return;
            PostToUi(() =>
            {
                if (Kernel.Pool.Slots.TryGetValue(health.SlotId, out var slot))
                    slot.WatchdogHealth = health;
            });
        }

        /// <summary>
        /// 卡槽恢复到 Online 后补一次 eUICC 探测。
        ///
        /// 背景：切卡会触发 eUICC 发起 SIM REFRESH，读卡器在数秒内不可用，
        /// 探测会拿到 6A81 / 6881 / 91 00 而失败。旧实现失败后不再重试，
        /// 界面上的 Profile 列表就一直是空的。这里在卡槽恢复时补一次，
        /// 若上一次探测本来就是 Supported 且已有数据，则跳过以免无谓锁卡。
        /// </summary>
        private async Task ReprobesEuiccAfterOnlineAsync(ModemSlot slot)
        {
            try
            {
                if (slot.IsProfileSwitchInProgress) return;
                if (slot.LastEuiccProbe is { Capability: EuiccCapability.Supported } cached &&
                    cached.Profiles.Count > 0)
                {
                    // 已有可信结果，交给 ViewModel 的失败重试链处理即可。
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                if (slot.IsProfileSwitchInProgress) return;

                var result = await ProbeEuiccAsync(slot.Id).ConfigureAwait(false);
                // 启动扫描这条路径不会自动触发 EuiccProbeCompleted，界面侧栏只能
                // 依赖缓存轮询；这里显式广播，保证 Profile 列表能真正填充。
                EuiccProbeCompleted?.Invoke(slot.Id, result);
            }
            catch (Exception ex)
            {
                AddLog("WARN", "eSIM", $"卡槽 {slot.Id} 恢复后重探 eUICC 失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 切卡后的延迟重读：等 SIM REFRESH 落地、卡槽回到 Online，再探测 eUICC，
        /// 最后通过 <see cref="EuiccProbeCompleted"/> 把结果交给界面。
        ///
        /// 放在服务层而不是页面里，是因为切卡可能由托盘热键发起，
        /// 那时设备管理页根本没打开；由服务统一负责，界面何时打开都能拿到最新列表。
        /// </summary>
        private async Task ReloadEuiccAfterSwitchAsync(ModemSlot slot)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline)
                {
                    if (slot.IsProfileSwitchInProgress)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                        continue;
                    }
                    if (slot.State == SlotState.Online) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }

                // 切卡会触发 SIM REFRESH，读卡器在数秒内不可用，首次探测可能失败。
                // 改为带重试循环：最多 5 次，间隔 1s/1s/2s/2s/3s（累计 9s），
                // 覆盖复位窗口（12-15s）后立即探测成功，避免长时间失败重试。
                var retryDelays = new[]
                {
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(3)
                };
                for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
                {
                    if (attempt > 0)
                        await Task.Delay(retryDelays[attempt - 1]).ConfigureAwait(false);

                    if (slot.IsProfileSwitchInProgress)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                        continue;
                    }

                    var result = await ProbeEuiccAsync(slot.Id).ConfigureAwait(false);
                    AddLog("INFO", "eSIM", $"切卡后重读 eUICC（第 {attempt + 1} 次）：{result.Message}（Profile {result.Profiles.Count} 个）");
                    EuiccProbeCompleted?.Invoke(slot.Id, result);
                    if (result.Capability == EuiccCapability.Supported && result.Profiles.Count > 0)
                        return;
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", "eSIM", $"切卡后重读 eUICC 失败（{slot.Name}）：{ex.Message}");
            }
        }

        private async Task StopWatchdogsAsync()
        {
            if (_watchdogRegistry != null)
                _watchdogRegistry.HealthChanged -= OnWatchdogHealthChanged;
            try { if (_globalWatchdog != null) await _globalWatchdog.DisposeAsync().ConfigureAwait(false); } catch { }
            try { if (_poolWatchdog != null) await _poolWatchdog.DisposeAsync().ConfigureAwait(false); } catch { }
            try { if (_slotWatchdog != null) await _slotWatchdog.DisposeAsync().ConfigureAwait(false); } catch { }
            _globalWatchdog = null;
            _poolWatchdog = null;
            _slotWatchdog = null;
            _watchdogRegistry = null;
        }

        private void RegisterKernelEvents()
        {
            // Call Events
            Kernel.CallStateChanged += (s, e) =>
            {
                if (e.NewState is not (CallState.Ended or CallState.Idle))
                {
                    var key = CallEventKey(e.SlotId, e.CallId);
                    if (!_recordedCallEvents.ContainsKey(key))
                        _callEventMetadata.TryAdd(key,
                            (e.TargetNumber, DateTime.UtcNow, e.IsOutgoing ? CallDirection.Outgoing : CallDirection.Incoming));
                }
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (e.NewState is CallState.Ended or CallState.Idle)
                    {
                        // CallEnded owns final presentation and recording. A
                        // different slot's state transition must not dismiss
                        // the call currently shown on the desktop.
                        return;
                    }
                    if (!string.Equals(Kernel.ActiveCall?.CallId, e.CallId, StringComparison.OrdinalIgnoreCase)) return;
                    _presentedCallId = e.CallId;
                    CurrentCallState = e.NewState;
                    CurrentCallNumber = e.TargetNumber;

                    if (e.NewState == CallState.Ringing && e.IsOutgoing)
                    {
                        _callAlerting.StartRingback();
                    }
                    else if (e.IsOutgoing && e.OldState == CallState.Ringing)
                    {
                        _callAlerting.StopRingback();
                    }

                    if (e.NewState == CallState.Active)
                    {
                        HasIncomingCall = false;
                        _shownIncomingCallId = null;
                        Views.Windows.IncomingCallFloatingWindow.Dismiss();
                    }

                    AddLog("INFO", "Calls", $"Call on slot {e.SlotId ?? "fallback"} state changed -> {e.NewState}");
                }));
            };

            Kernel.IncomingCall += (s, e) =>
            {
                var key = CallEventKey(e.SlotId, e.CallId);
                if (!_recordedCallEvents.ContainsKey(key))
                    _callEventMetadata.TryAdd(key, (e.CallerNumber, e.Timestamp, CallDirection.Incoming));
                try { IncomingCallReceived?.Invoke(e.CallerNumber, e.SlotId); } catch { }
                PostToUi(() =>
                {
                    if (!string.Equals(Kernel.ActiveCall?.CallId, e.CallId, StringComparison.OrdinalIgnoreCase)) return;
                    AddLog("INFO", "Calls", $"Incoming call on slot {e.SlotId ?? "fallback"}; caller omitted from diagnostics.");
                    PresentIncomingCall(e.CallId, e.CallerNumber, e.SlotId);
                });
            };

            Kernel.CallConnected += async (s, e) =>
            {
                var key = CallEventKey(e.SlotId, e.CallId);
                if (_recordedCallEvents.ContainsKey(key)) return;
                _callEventMetadata.TryAdd(key, (e.TargetNumber, e.ConnectedAt, CallDirection.Outgoing));
                _connectedCallEvents[key] = e;
                if (!string.Equals(Kernel.ActiveCall?.CallId, e.CallId, StringComparison.OrdinalIgnoreCase))
                {
                    AddLog("INFO", "Calls", $"Call {e.CallId} connected on a background slot; desktop audio remains with the presented call.");
                    return;
                }
                PostToUi(() =>
                {
                    if (!string.Equals(Kernel.ActiveCall?.CallId, e.CallId, StringComparison.OrdinalIgnoreCase)) return;
                    _presentedCallId = e.CallId;
                    StopIncomingAlerting();
                    CurrentCallState = CallState.Active;
                    HasIncomingCall = false;
                    _shownIncomingCallId = null;
                    Views.Windows.IncomingCallFloatingWindow.Dismiss();
                    AddLog("INFO", "Calls", $"Call connected on slot {e.SlotId ?? "fallback"} (Codec: {e.Codec ?? "Default"})");
                });

                await EnsureDesktopMediaForCallAsync(e);
            };

            Kernel.CallEnded += (s, e) =>
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    Task audioCleanup = Task.CompletedTask;
                    var desktopAudioKind = ReleaseDesktopAudioClaim(e.CallId, e.SlotId);
                    if (desktopAudioKind == DesktopAudioKind.CellularUsb)
                    {
                        var audioCts = Interlocked.Exchange(ref _cellularCallAudioCts, null);
                        audioCts?.Cancel();
                        audioCts?.Dispose();
                        audioCleanup = StopDesktopCellularAudioAsync();
                    }
                    else if (desktopAudioKind == DesktopAudioKind.Microphone)
                    {
                        StopMicrophone();
                    }
                    else if (IsReserved(e.SlotId))
                    {
                        // The SIP gateway owns this UAC endpoint. Release only
                        // its reservation, never a different desktop call.
                        audioCleanup = ReleaseAsync(e.SlotId);
                    }

                    var key = CallEventKey(e.SlotId, e.CallId);
                    _connectedCallEvents.TryRemove(key, out _);
                    var hasMetadata = _callEventMetadata.TryRemove(key, out var metadata);
                    var dur = e.Duration ?? (hasMetadata ? e.EndedAt - metadata.Started : TimeSpan.Zero);
                    if (_recordedCallEvents.TryAdd(key, e.EndedAt))
                        AddCallRecord(e.TargetNumber,
                            hasMetadata ? metadata.Direction : CallDirection.Outgoing,
                            CallState.Ended,
                            hasMetadata ? metadata.Started : e.EndedAt - dur,
                            dur, null, e.SlotId, e.WavRecordingPath);
                    if (_recordedCallEvents.Count > 4096)
                        foreach (var old in _recordedCallEvents.Where(item => item.Value < DateTime.UtcNow.AddDays(-1)))
                            _recordedCallEvents.TryRemove(old.Key, out _);

                    if (string.Equals(_presentedCallId, e.CallId, StringComparison.OrdinalIgnoreCase))
                    {
                        StopIncomingAlerting();
                        _callAlerting.StopRingback();
                        var next = Kernel.ActiveCall;
                        if (next == null)
                        {
                            CurrentCallState = CallState.Ended;
                            CurrentCallNumber = e.TargetNumber;
                            HasIncomingCall = false;
                            _shownIncomingCallId = null;
                            Views.Windows.IncomingCallFloatingWindow.Dismiss();
                            Views.Windows.InCallFloatingWindow.Dismiss();
                        }
                        else if (next.State == CallState.Incoming)
                        {
                            PresentIncomingCall(next.CallId, next.RemoteNumber, Kernel.ActiveCallSlotId);
                        }
                        else
                        {
                            _presentedCallId = next.CallId;
                            CurrentCallState = next.State;
                            CurrentCallNumber = next.RemoteNumber;
                            HasIncomingCall = false;
                            _shownIncomingCallId = null;
                            Views.Windows.IncomingCallFloatingWindow.Dismiss();
                        }
                        if (next?.State == CallState.Active &&
                            _connectedCallEvents.TryGetValue(CallEventKey(Kernel.ActiveCallSlotId, next.CallId), out var connected))
                            SafeFire.Run(ResumePromotedCallAudioAsync(audioCleanup, connected));
                        ApplyVolteAudioPrewarmPreference();
                    }
                    AddLog("INFO", "Calls",
                        $"Call ended on slot {e.SlotId ?? "fallback"}, duration: {dur:mm\\:ss}; reason omitted from diagnostics.");
                }));
            };

            // SMS Events
            Kernel.SmsReceived += (s, e) =>
            {
                SafeFire.Run(AddIncomingSmsAsync(e.Message, e.SlotId));
                AddLog("INFO", "SMS", $"Received SMS on slot {e.SlotId ?? "unknown"}; body omitted from diagnostics.");
            };

            Kernel.SmsSent += (s, e) =>
            {
                AddLog("INFO", "SMS", $"SMS submission status: {e.Result.SubmissionStatus}");
            };

            Kernel.SmsStatusReportReceived += (s, e) =>
            {
                UpdateSmsDeliveryReport(e.Report);
                AddLog("INFO", "SMS", $"SMS Delivery Report for ref #{e.Report.MessageReference} -> {e.Report.DeliveryStatus}");
            };

            // Slot & Pool Events
            Kernel.SlotListChanged += (s, e) =>
            {
                RequestSlotSync();
            };
            Kernel.Pool.SlotRemoved += (s, slotId) => PostToUi(() => ReconcileRemovedCallSlot(slotId));

            Kernel.ActiveSlotChanged += (s, e) =>
            {
                RequestSlotSync();
                AddLog("INFO", "ModemPool", $"Active slot changed to: {e.ActiveSlotId}");
            };

            Kernel.SlotStateChanged += (s, e) =>
            {
                RequestSlotSync();
                AddLog("INFO", "ModemPool", $"Slot {e.SlotId} state -> {e.NewState}");
                if (e.NewState == SlotState.Error && e.Slot != null)
                {
                    AddLog("ERROR", "ModemPool", $"Slot {e.SlotId} error: {e.Slot.LastError}");
                }
                if (e.NewState == SlotState.Online)
                {
                    var slot = Slots.FirstOrDefault(x => x.Id == e.SlotId);
                    if (slot != null)
                    {
                        SafeFire.Run(ApplyPreferencesToSlotAsync(slot));
                        ScheduleIccidIdentityRefresh();

                        // 卡槽回到 Online 说明 SIM REFRESH / 读卡器 detach 已结束，
                        // 此时上一次 eUICC 探测很可能是在锁定窗口里失败的。
                        // 主动补一次探测，让 eSIM 列表自动恢复，不必等用户手动刷新。
                        SafeFire.Run(ReprobesEuiccAfterOnlineAsync(slot));
                    }
                }
            };

            // State Machine & Diagnostics
            Kernel.TelephonyStateChanged += (s, e) =>
            {
                AddLog("INFO", "StateMachine", $"State transition: {e.From} -> {e.To}");
            };

            Kernel.VoWifiStateChanged += (s, e) =>
            {
                AddLog("INFO", "VoWiFi", $"VoWiFi state: {e.NewState}");
                ScheduleIccidIdentityRefresh();
            };

            Kernel.LogEmitted += (s, e) =>
            {
                AddLog(e.Level, e.Source, e.Message);
                UpdateInitialStartupProgress(e.Source, e.Message);
            };

            Kernel.SystemErrorOccurred += (s, e) =>
            {
                AddLog("ERROR", e.Source, e.ErrorMessage);
            };
        }

        private void UpdateInitialStartupProgress(string source, string message)
        {
            if (!IsInitialScanRunning || !source.Equals("SIM", StringComparison.OrdinalIgnoreCase))
                return;

            string? status = null;
            if (message.Contains("startup stage=SIM readiness", StringComparison.OrdinalIgnoreCase))
            {
                status = message.Contains("ADF.USIM=selected", StringComparison.OrdinalIgnoreCase)
                    ? "SIM 已响应，正在确认 USIM 应用稳定性…"
                    : "正在等待 SIM 卡与 USIM 应用就绪…";
            }
            else if (message.Contains("startup SIM readiness timed out", StringComparison.OrdinalIgnoreCase))
            {
                status = "SIM 初始化较慢；正在保留设备并等待后续重试…";
            }

            if (status is not null)
                PostToUi(() => InitialScanStatus = status, DispatcherPriority.DataBind);
        }

        private void RequestSlotSync()
        {
            Interlocked.Increment(ref _slotSyncRequestVersion);
            ScheduleSlotSync();
        }

        private void ScheduleSlotSync()
        {
            if (Interlocked.Exchange(ref _slotSyncScheduled, 1) != 0)
            {
                return;
            }

            PostToUi(() =>
            {
                var handledVersion = Volatile.Read(ref _slotSyncRequestVersion);
                try
                {
                    SyncSlots();
                }
                finally
                {
                    Interlocked.Exchange(ref _slotSyncScheduled, 0);
                    if (handledVersion != Volatile.Read(ref _slotSyncRequestVersion))
                    {
                        ScheduleSlotSync();
                    }
                }
            }, DispatcherPriority.DataBind);
        }

        private void SyncSlots()
        {
            var kernelSlots = Kernel.GetSlots().ToList();

            var toRemove = Slots.Where(s => !kernelSlots.Any(k => k.Id == s.Id)).ToList();
            foreach (var r in toRemove) Slots.Remove(r);

            foreach (var k in kernelSlots)
            {
                if (!Slots.Any(s => s.Id == k.Id))
                {
                    Slots.Add(k);
                }
            }

            ApplyCallRecordingPreference();
        }

        private void AddLog(string level, string source, string message)
        {
            var now = DateTime.Now;
            message = DiagnosticLogRedactor.Redact(message);
            _pendingUiLogs.Enqueue(new LogEntryModel
            {
                Timestamp = now,
                Level = level,
                Source = source,
                Message = message
            });

            ScheduleLogDrain();
            _logLines.Writer.TryWrite($"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{source}] {message}");
        }

        private void ScheduleLogDrain()
        {
            if (Interlocked.Exchange(ref _logDrainScheduled, 1) != 0)
            {
                return;
            }

            PostToUi(DrainPendingLogs, DispatcherPriority.Background);
        }

        private void DrainPendingLogs()
        {
            const int maxBatchSize = 100;
            var drained = 0;

            while (drained < maxBatchSize && _pendingUiLogs.TryDequeue(out var entry))
            {
                Logs.Add(entry);
                drained++;
            }

            while (Logs.Count > 500)
            {
                Logs.RemoveAt(0);
            }

            Interlocked.Exchange(ref _logDrainScheduled, 0);
            if (!_pendingUiLogs.IsEmpty)
            {
                ScheduleLogDrain();
            }
        }

        private static string GetLogFilePath()
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoWin",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            return Path.Combine(logDirectory, "vowin.log");
        }

        private async Task RunLogWriterAsync(CancellationToken cancellationToken)
        {
            // 日志写入失败必须留下痕迹。静默 catch 会让"界面正常但完全没有
            // 日志"这种故障变得无从诊断——曾经的排查就卡在这里。
            var failureReported = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var stream = new FileStream(
                        GetLogFilePath(),
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        16 * 1024,
                        useAsync: true);
                    await using var writer = new StreamWriter(stream) { AutoFlush = false };

                    if (Interlocked.Exchange(ref failureReported, 0) == 1)
                        await writer.WriteLineAsync(
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] [Log] 日志文件已恢复写入。")
                            .ConfigureAwait(false);

                    while (await _logLines.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (_logLines.Reader.TryRead(out var line))
                        {
                            await writer.WriteLineAsync(line).ConfigureAwait(false);
                        }

                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 记录到独立通道：主日志不可用时仍能看到原因。
                    if (Interlocked.Exchange(ref failureReported, 1) == 0)
                    {
                        ReportLogFailure(ex);
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 主日志通道不可写时的旁路告警。写入 %LOCALAPPDATA%\VoWin\Diagnostics，
        /// 若该目录同样不可写则退化为 OutputDebugString，绝不抛出异常。
        /// </summary>
        private static void ReportLogFailure(Exception ex)
        {
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [LogWriter] 日志写入失败: {ex.GetType().Name}: {ex.Message}";
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VoWin",
                    "Diagnostics");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "log-writer-failure.log"), text + Environment.NewLine);
            }
            catch
            {
                try { System.Diagnostics.Debug.WriteLine(text); } catch { }
            }
        }

        private void AddCallRecord(string number, CallDirection dir, CallState state, DateTime startTime, TimeSpan duration, string? codec = null, string? slotId = null, string? wavPath = null)
        {
            var record = new CallRecordModel
            {
                PhoneNumber = number,
                Direction = dir,
                FinalState = state,
                Timestamp = startTime,
                Duration = duration,
                Codec = codec,
                SlotId = slotId,
                WavRecordingPath = wavPath
            };

            PostToUi(() => CallHistory.Insert(0, record));
            _ = Preferences.SaveCallRecordAsync(record);
        }

        public void DeleteCallRecord(string id)
        {
            PostToUi(() =>
            {
                var item = CallHistory.FirstOrDefault(c => c.Id == id);
                if (item != null)
                {
                    CallHistory.Remove(item);
                    AddLog("INFO", "Calls", $"Deleted call record {id}");
                }
            });
            _ = Preferences.DeleteCallRecordAsync(id);
        }

        private void PostToUi(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _dispatcher.BeginInvoke(action, priority);
            }
        }

        private Task RunOnUiAsync(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (_dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            return _dispatcher.InvokeAsync(action, priority).Task;
        }

        private void AddIncomingSms(SmsMessage msg, string? slotId) => SafeFire.Run(AddIncomingSmsAsync(msg, slotId));

        private async Task AddIncomingSmsAsync(SmsMessage msg, string? slotId)
        {
            var remote = !string.IsNullOrWhiteSpace(msg.SenderOrRecipient) ? msg.SenderOrRecipient : "Unknown";
            var receivedAtUtc = DateTime.UtcNow;
            var normalizedTimestamp = TimestampDisplayHelper.NormalizeIncomingNetworkTime(msg.Timestamp, receivedAtUtc);
            if (normalizedTimestamp != TimestampDisplayHelper.ToUtcStorageTime(msg.Timestamp))
            {
                AddLog("INFO", "SMS", $"Corrected network timestamp from {msg.Timestamp:o} to {normalizedTimestamp:o}");
            }

            var exists = await Preferences.HasSmsMessageAsync(remote, normalizedTimestamp, msg.Text, msg.RawPdu).ConfigureAwait(false);
            if (exists)
            {
                return;
            }

            var item = new SmsMessageModel
            {
                Index = msg.Index,
                SlotId = slotId,
                SenderOrRecipient = remote,
                Text = msg.Text,
                Timestamp = normalizedTimestamp,
                IsOutgoing = false,
                DeliveryState = SmsDeliveryState.Received,
                DeliveryStatus = msg.DeliveryStatus,
                MessageReference = msg.MessageReference,
                RawPdu = msg.RawPdu
            };

            await Preferences.SaveSmsMessageAsync(item).ConfigureAwait(false);
            try { IncomingSmsReceived?.Invoke(item); } catch { }

            await RunOnUiAsync(() =>
            {
                var conv = GetOrCreateConversation(remote);
                if (!conv.Messages.Any(m => m.Timestamp == item.Timestamp && m.Text == item.Text))
                {
                    conv.Messages.Add(item);
                    conv.UnreadCount++;
                    conv.UpdateLastMessage();
                    MoveConversationToTop(conv);
                }

                // If incoming SMS contains verification code (OTP): play alert chime and show floating notification window
                if (item.HasOtpCode)
                {
                    var slotName = !string.IsNullOrEmpty(slotId)
                        ? Slots.FirstOrDefault(s => s.Id.Equals(slotId, StringComparison.OrdinalIgnoreCase))?.Name
                        : ActiveSlot?.Name ?? "SIM 1";

                    SoundEffectService.Instance.PlaySmsChime();
                    Views.Windows.IncomingSmsFloatingWindow.ShowNotification(item, slotName);
                }
            }).ConfigureAwait(false);
        }

        private void UpdateSmsDeliveryReport(SmsStatusReport report)
        {
            PostToUi(() =>
            {
                var conv = Conversations.FirstOrDefault(c => c.ContactNumber == report.Recipient);
                if (conv != null)
                {
                    var msg = conv.Messages.LastOrDefault(m => m.IsOutgoing && m.MessageReference == report.MessageReference);
                    if (msg != null)
                    {
                        msg.DeliveryState = report.StatusCode == 0 ? SmsDeliveryState.Delivered : SmsDeliveryState.Failed;
                        msg.DeliveryStatus = report.DeliveryStatus;
                        conv.UpdateLastMessage();
                        _ = Preferences.UpdateSmsDeliveryStatusAsync(msg.Id, msg.DeliveryState, report.DeliveryStatus);
                    }
                }
            });
        }

        private SmsConversationModel GetOrCreateConversation(string number)
        {
            var clean = number.Trim();
            var conv = Conversations.FirstOrDefault(c => c.ContactNumber.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (conv == null)
            {
                string[] colors = { "#0078D4", "#107C10", "#D13438", "#8764B8", "#FF8C00", "#008272", "#E3008C" };
                int hash = Math.Abs(clean.GetHashCode());
                var letter = clean.Length > 0 ? (char.IsLetter(clean[0]) ? clean[0].ToString().ToUpper() : clean[^1].ToString()) : "#";

                conv = new SmsConversationModel
                {
                    ContactNumber = clean,
                    AvatarLetter = letter,
                    AvatarColor = colors[hash % colors.Length]
                };
                Conversations.Add(conv);
            }
            return conv;
        }

        private void MoveConversationToTop(SmsConversationModel conv)
        {
            int oldIdx = Conversations.IndexOf(conv);
            if (oldIdx > 0)
            {
                Conversations.Move(oldIdx, 0);
            }
            else if (oldIdx < 0)
            {
                Conversations.Insert(0, conv);
            }
        }

        // Operation implementations

        public async Task<IReadOnlyList<ModemSlot>> DiscoverSlotsAsync()
        {
            AddLog("INFO", "ModemPool", "Scanning system COM ports for modem hardware...");
            var slots = await DiscoverSlotsCoreAsync();
            await RunOnUiAsync(SyncSlots);
            return slots;
        }

        private async Task<IReadOnlyList<ModemSlot>> DiscoverSlotsCoreAsync(CancellationToken cancellationToken = default)
        {
            await _slotDiscoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                    async () => await Kernel.DiscoverSlotsAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _slotDiscoveryGate.Release();
            }
        }

        private async Task<IReadOnlyList<ModemSlot>> DiscoverPcscSlotsCoreAsync(CancellationToken cancellationToken = default)
        {
            await _slotDiscoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                    async () => await Kernel.DiscoverPcscSlotsAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _slotDiscoveryGate.Release();
            }
        }

        private async Task<IReadOnlyList<ModemSlot>> DiscoverSerialSlotsCoreAsync(CancellationToken cancellationToken = default)
        {
            await _slotDiscoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                    async () => await Kernel.DiscoverSerialSlotsAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _slotDiscoveryGate.Release();
            }
        }

        /// <summary>
        /// Probes eUICC chips and restores auto-VoWiFi / radio preferences for a
        /// set of slots. Shared by the device-type-first boot path (PC/SC reader
        /// first, deferred serial-modem scan second).
        /// </summary>
        private async Task ApplyProbeAndPreferencesAsync(IReadOnlyList<ModemSlot> slots)
        {
            // The eUICC probe is read-only and happens before restoring
            // auto-VoWiFi.  This both makes eSIM support visible at launch
            // and avoids competing APDU traffic with an automatic IMS start.
            InitialScanStatus = "正在识别 eSIM 芯片…";
            foreach (var slot in slots)
            {
                await ProbeEuiccAsync(slot.Id).ConfigureAwait(false);
            }

            InitialScanStatus = "正在恢复模块设置…";
            foreach (var slot in slots)
            {
                await ApplyPreferencesToSlotAsync(slot);
            }

            // 启动即把活卡纳入 ICCID 规则表（自动补行），号码/卡名随后回填。
            ScheduleIccidIdentityRefresh();

            // Standard USB Audio Class modems never need ADB. Only probe the
            // compatibility runtime when the modem explicitly identifies as
            // QDC507; otherwise even looking for adb.exe is unwanted work.
            if (slots.Any(slot => slot.Modem?.FirmwareRevision?.Contains(
                    "QDC507", StringComparison.OrdinalIgnoreCase) == true))
                SafeFire.Run(PrepareQdc507VoiceRuntimeAsync());
        }

        public async Task<ModemSlot?> AddSlotAsync(string portName, int baudRate = 115200, string? name = null, string? proxyUrl = null)
        {
            AddLog("INFO", "ModemPool", $"Adding modem slot on {portName} ({baudRate} bps)...");
            var slot = await Kernel.AddSlotAsync(portName, baudRate, name, proxyUrl: proxyUrl);
            await RunOnUiAsync(SyncSlots);
            return slot;
        }

        public async Task<bool> RemoveSlotAsync(string slotId)
        {
            AddLog("INFO", "ModemPool", $"Removing slot {slotId}...");
            bool ok = await Kernel.RemoveSlotAsync(slotId);
            await RunOnUiAsync(SyncSlots);
            return ok;
        }

        public bool SelectSlot(string slotId)
        {
            bool ok = Kernel.SelectSlot(slotId);
            RequestSlotSync();
            return ok;
        }

        public async Task RefreshMetricsAsync(string? slotId = null)
        {
            var slot = !string.IsNullOrWhiteSpace(slotId)
                ? Kernel.Pool.Slots.GetValueOrDefault(slotId) ?? Kernel.Pool.ActiveSlot
                : Kernel.Pool.ActiveSlot;
            if (slot != null)
            {
                await slot.RefreshMetricsAsync().ConfigureAwait(false);
                return;
            }
            await Kernel.RefreshSignalAsync(slotId).ConfigureAwait(false);
            await Kernel.RefreshRegistrationAsync(slotId).ConfigureAwait(false);
        }

        public Task<SimIdentity?> RefreshSimAsync(string? slotId = null) =>
            Kernel.RefreshSimAsync(slotId);

        public async Task<string> ExecuteAtCommandAsync(string command, string? slotId = null)
        {
            var trimmed = command.Trim();
            var loggedCommand = System.Text.RegularExpressions.Regex.Replace(
                trimmed,
                @"(?i)^(AT\+CPIN\s*=).*$",
                "$1<redacted>");
            AddLog("INFO", "AT", $">> {loggedCommand}");

            try
            {
                ModemSlot? targetSlot = null;
                if (!string.IsNullOrEmpty(slotId))
                {
                    Kernel.Pool.Slots.TryGetValue(slotId, out targetSlot);
                }
                else
                {
                    targetSlot = Kernel.Pool.ActiveSlot;
                }

                if (targetSlot?.IsPcscReader == true)
                {
                    const string message = "PC/SC 卡槽不提供 AT 指令；可使用 eSIM/USIM 管理、VoWiFi、IMS 通话和短信。";
                    AddLog("WARN", "PC/SC", message);
                    return $"ERROR: {message}";
                }

                if (targetSlot?.Modem != null && targetSlot.Modem.IsOpen)
                {
                    var resp = await targetSlot.Modem.SendRawAtCommandAsync(trimmed, 5000);
                    var output = resp.RawOutput ?? string.Join("\r\n", resp.Lines);
                    AddLog("INFO", "AT", $"<< {output}");
                    return output;
                }
                else
                {
                    // Fallback to Kernel execute command
                    var res = await Kernel.ExecuteCommandAsync(trimmed);
                    AddLog("INFO", "AT", $"<< [{res.Success}] {res.Message}");
                    return res.Message;
                }
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "AT", $"<< ERROR: {ex.Message}");
                return $"ERROR: {ex.Message}";
            }
        }

        public async Task<string> SendUssdAsync(string code, string? slotId = null)
        {
            AddLog("INFO", "USSD", $">> Sending USSD code: {code}");
            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                {
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                }
                else
                {
                    slot = Kernel.Pool.ActiveSlot;
                }

                if (slot?.IsPcscReader == true)
                {
                    const string message = "PC/SC 卡槽不支持 USSD；请在 VoWiFi 注册后使用 IMS 通话和短信。";
                    AddLog("WARN", "PC/SC", message);
                    return $"ERROR: {message}";
                }

                if (slot != null)
                {
                    var result = await slot.SendUssdAsync(code);
                    AddLog("INFO", "USSD", $"<< {result}");
                    return result;
                }
                return "未找到可用卡槽或调制解调器未就绪。";
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "USSD", $"<< USSD 异常: {ex.Message}");
                return $"ERROR: {ex.Message}";
            }
        }

        public async Task<bool> SetFlightModeAsync(bool enable, string? slotId = null)
        {
            AddLog("INFO", "Modem", $"设置飞行模式: {enable}");
            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                {
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                }
                else
                {
                    slot = Kernel.Pool.ActiveSlot;
                }

                if (slot?.IsPcscReader == true)
                {
                    AddLog("WARN", "PC/SC", "PC/SC 卡槽没有蜂窝射频，无法设置飞行模式。");
                    return false;
                }

                if (slot != null)
                {
                    // Persist the user's intent before touching CFUN. Otherwise a
                    // later CGATT/metrics failure can leave the old preference in
                    // SQLite and the next modem attach will undo a confirmed toggle.
                    await PersistRuntimeIntentAsync(slot, flightMode: enable).ConfigureAwait(false);
                    var applied = await slot.SetFlightModeAsync(enable);
                    if (!applied || slot.IsFlightMode != enable)
                        await PersistRuntimeIntentAsync(slot, flightMode: slot.IsFlightMode).ConfigureAwait(false);
                    AddLog(applied ? "INFO" : "WARN", "Modem", applied
                        ? $"飞行模式已确认{(enable ? "开启 (CFUN=4)" : "关闭 (CFUN=1)")}。"
                        : $"飞行模式未生效：模组没有确认 CFUN={(enable ? 4 : 1)}。偏好未更新。");
                    return applied;
                }
                AddLog("WARN", "Modem", "设置飞行模式失败：未找到目标卡槽。");
                return false;
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "Modem", $"设置飞行模式失败: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> RebootModemAsync(string? slotId = null)
        {
            AddLog("INFO", "Modem", "发送模组重启指令...");
            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                {
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                }
                else
                {
                    slot = Kernel.Pool.ActiveSlot;
                }

                if (slot?.IsPcscReader == true)
                {
                    AddLog("WARN", "PC/SC", "PC/SC 卡槽没有可重启的 Modem。");
                    return false;
                }

                if (slot != null)
                {
                    return await slot.RebootAsync();
                }
                return false;
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "Modem", $"模组重启失败: {ex.Message}");
                return false;
            }
        }

        public async Task<CallInfo> DialAsync(string number, string? slotId = null, bool forceCellular = false)
        {
            AddLog("INFO", "Calls", $"Dialing on slot {slotId ?? "selected"} (ForceCellular={forceCellular}); number omitted from diagnostics.");
            var presentDial = Kernel.ActiveCall == null;
            if (presentDial)
            {
                CurrentCallNumber = number;
                CurrentCallState = CallState.Dialing;
            }

            try
            {
                ModemSlot? slot = null;
                if (!string.IsNullOrEmpty(slotId))
                    Kernel.Pool.Slots.TryGetValue(slotId, out slot);
                else
                    slot = Kernel.Pool.ActiveSlot;

                if ((!forceCellular && slot?.VoWifi?.State == VoWifiState.ImsRegistered) ||
                    slot?.Modem is { IsOpen: true })
                {
                    return await Kernel.DialAsync(number, slotId, forceCellular);
                }
                else
                {
                    throw new InvalidOperationException("Selected slot has neither registered VoWiFi nor an open cellular modem.");
                }
            }
            catch (Exception ex)
            {
                if (presentDial && Kernel.ActiveCall == null && _presentedCallId == null)
                    CurrentCallState = CallState.Ended;
                AddLog("ERROR", "Calls", $"Dial failed ({ex.GetType().Name}); see call status for details.");
                throw;
            }
        }

        public async Task<CallInfo?> HangupAsync(string? slotId = null)
        {
            AddLog("INFO", "Calls", "Hanging up current call...");
            var res = await Kernel.HangupAsync(slotId ?? Kernel.ActiveCallSlotId);
            if (res == null && CurrentCallState is not (CallState.Idle or CallState.Ended))
                throw new InvalidOperationException("Call hangup was not confirmed by the modem or IMS session.");
            // The matching CallEnded event owns UI/media cleanup and history.
            // A caller may hang up a background slot while another call remains.
            return res;
        }

        public async Task<CallInfo?> AnswerAsync(string? slotId = null)
        {
            AddLog("INFO", "Calls", "Answering incoming call...");
            try
            {
                return await Kernel.AnswerAsync(slotId ?? Kernel.ActiveCallSlotId);
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "Calls", $"Answer failed ({ex.GetType().Name}).");
                return null;
            }
        }

        public async Task<CallInfo?> RejectAsync(string? slotId = null)
        {
            AddLog("INFO", "Calls", "Rejecting incoming call...");
            var effectiveSlotId = slotId ?? Kernel.ActiveCallSlotId;
            var current = Kernel.ActiveCall;
            if (current?.State == CallState.Incoming)
            {
                var key = CallEventKey(effectiveSlotId, current.CallId);
                _callEventMetadata.AddOrUpdate(key,
                    (current.RemoteNumber, current.StartTime, CallDirection.Missed),
                    (_, old) => (old.Number, old.Started, CallDirection.Missed));
            }

            try
            {
                return await Kernel.RejectAsync(effectiveSlotId);
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Phone", $"Reject call failed: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> SendDtmfAsync(char digit, string? slotId = null)
        {
            AddLog("INFO", "Calls", "Sending DTMF digit; value omitted from diagnostics.");
            return await Kernel.SendDtmfAsync(digit, slotId);
        }

        public async Task<SmsSubmitResult> SendSmsAsync(string recipient, string text, bool requestStatusReport = true, string? slotId = null, bool forceVowifi = false, bool forceCellular = false)
        {
            AddLog("INFO", "SMS", $"Sending SMS on slot {slotId ?? "selected"}; recipient and body omitted from diagnostics.");

            SmsConversationModel conv = null!;
            SmsMessageModel model = null!;
            await RunOnUiAsync(() =>
            {
                conv = GetOrCreateConversation(recipient);
                model = new SmsMessageModel
                {
                    SenderOrRecipient = recipient,
                    Text = text,
                    Timestamp = DateTime.UtcNow,
                    IsOutgoing = true,
                    DeliveryState = SmsDeliveryState.Sending
                };
                conv.Messages.Add(model);
                conv.UpdateLastMessage();
                MoveConversationToTop(conv);
            });

            await Preferences.SaveSmsMessageAsync(model);

            try
            {
                var result = await Kernel.SendSmsAsync(recipient, text, requestStatusReport, slotId, forceVowifi, forceCellular);
                await RunOnUiAsync(() =>
                {
                    model.DeliveryState = result.AllPartsAccepted ? SmsDeliveryState.Sent : SmsDeliveryState.Failed;
                    model.DeliveryStatus = result.SubmissionStatus;
                    model.MessageReference = result.ConcatReference;
                    conv.UpdateLastMessage();
                });
                await Preferences.UpdateSmsDeliveryStatusAsync(model.Id, model.DeliveryState, model.DeliveryStatus);
                return result;
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() =>
                {
                    model.DeliveryState = SmsDeliveryState.Failed;
                    model.DeliveryStatus = ex.Message;
                    conv.UpdateLastMessage();
                });
                await Preferences.UpdateSmsDeliveryStatusAsync(model.Id, SmsDeliveryState.Failed, ex.Message);
                AddLog("ERROR", "SMS", $"Send SMS failed ({ex.GetType().Name}); recipient and body omitted from diagnostics.");
                throw;
            }
        }

        public void DeleteConversation(string contactNumber)
        {
            PostToUi(() =>
            {
                var conv = Conversations.FirstOrDefault(c => c.ContactNumber == contactNumber);
                if (conv != null)
                {
                    Conversations.Remove(conv);
                    AddLog("INFO", "SMS", $"Deleted conversation with {contactNumber}");
                }
            });
            _ = Preferences.DeleteConversationAsync(contactNumber);
        }

        public void ClearConversation(string contactNumber)
        {
            PostToUi(() =>
            {
                var conv = Conversations.FirstOrDefault(c => c.ContactNumber == contactNumber);
                if (conv != null)
                {
                    conv.Messages.Clear();
                    conv.UpdateLastMessage();
                    AddLog("INFO", "SMS", $"Cleared messages for conversation with {contactNumber}");
                }
            });
            _ = Preferences.DeleteConversationAsync(contactNumber);
        }

        public void DeleteMessage(string conversationNumber, string messageId)
        {
            PostToUi(() =>
            {
                var conv = Conversations.FirstOrDefault(c => c.ContactNumber == conversationNumber);
                if (conv != null)
                {
                    var msg = conv.Messages.FirstOrDefault(m => m.Id == messageId);
                    if (msg != null)
                    {
                        conv.Messages.Remove(msg);
                        conv.UpdateLastMessage();
                    }
                }
            });
            _ = Preferences.DeleteSmsMessageAsync(messageId);
        }

        public void ClearCallHistory()
        {
            PostToUi(CallHistory.Clear);
            _ = Preferences.ClearCallRecordsAsync();
            AddLog("INFO", "Calls", "Call history cleared.");
        }

        public async Task<bool> StartVoWifiAsync(string? slotId = null)
        {
            var targetSlot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (targetSlot == null) return false;
            _voWifiDesiredStates[GetVoWifiPreferenceKey(targetSlot)] = true;

            // 启动意图版本号是「最后一次用户操作胜出」的仲裁器。第二个调用者
            // 推进版本号，会让仍在进行的第一次启动判定自己已过期，从而主动
            // stop 掉刚建好的会话。L3 看门狗无条件拉起与启动自动注册正好构成
            // 这种成对调用，结果是每次开机都白白拆一次链。已有在途启动时
            // 等它结束并复用其结果，不推进版本号。
            if (targetSlot.IsVoWifiStartInFlight || targetSlot.VoWifi.State == VoWifiState.ImsRegistered)
            {
                // 等它结束；若那次启动最终失败，再按本次意图重新发起一次。
                if (await targetSlot.WaitForVoWifiStartAsync().ConfigureAwait(false)) return true;
            }

            var intentVersion = AdvanceVoWifiIntent(targetSlot.Id);
            await PersistRuntimeIntentAsync(targetSlot, voWifi: true).ConfigureAwait(false);
            return await StartVoWifiCoreAsync(targetSlot, intentVersion).ConfigureAwait(false);
        }

        public async Task<bool> StopVoWifiAsync(string? slotId = null)
        {
            var targetSlot = (!string.IsNullOrEmpty(slotId) ? Slots.FirstOrDefault(s => s.Id == slotId) : ActiveSlot) ?? Slots.FirstOrDefault();
            if (targetSlot == null) return false;
            _voWifiDesiredStates[GetVoWifiPreferenceKey(targetSlot)] = false;
            AdvanceVoWifiIntent(targetSlot.Id);
            _autoVoWifiStarts.TryRemove(targetSlot.Id, out _);
            await PersistRuntimeIntentAsync(targetSlot, voWifi: false).ConfigureAwait(false);
            AddLog("INFO", "VoWiFi", $"Stopping VoWiFi on slot {targetSlot.Id}...");
            return await Kernel.StopVoWifiAsync(targetSlot.Id).ConfigureAwait(false);
        }

        private async Task<bool> StartVoWifiCoreAsync(ModemSlot targetSlot, long expectedIntentVersion)
        {
            // Starting VoWiFi must apply identity/routing only. It must never
            // restore an old CFUN/CGATT preference as a side effect of Connect.
            await ApplyPreferencesToSlotAsync(targetSlot, restoreAutoVoWifi: false, restoreRadioPreferences: false)
                .ConfigureAwait(false);
            if (GetVoWifiIntentVersion(targetSlot.Id) != expectedIntentVersion) return false;

            var effectiveProxy = ResolveEgressProxyForSlot(targetSlot.Id);
            if (!Kernel.SetSlotProxy(targetSlot.Id, effectiveProxy))
            {
                AddLog("ERROR", "Proxy", $"Unable to apply the proxy route to slot {targetSlot.Id}.");
                return false;
            }

            targetSlot.ProxyUrl = effectiveProxy;
            AddLog("INFO", "VoWiFi", !string.IsNullOrEmpty(effectiveProxy)
                ? $"VoWiFi 代理路由已应用至核心: {DescribeProxyEndpoint(effectiveProxy)} -> 卡槽 [{targetSlot.Name}]"
                : $"VoWiFi 使用直连 UDP -> 卡槽 [{targetSlot.Name}]");
            if (string.IsNullOrWhiteSpace(effectiveProxy))
            {
                AddLog("INFO", "Proxy",
                    "当前为直连路由；若本轮注册失败，系统会在下次尝试时重新核算出口代理路由（配置了 ICCID/国家路由将自动转向 SOCKS5）。");
            }
            AddLog("INFO", "VoWiFi", $"Starting VoWiFi on slot {targetSlot.Id}...");
            var started = await Kernel.StartVoWifiAsync(targetSlot.Id).ConfigureAwait(false);

            if (!started && string.IsNullOrWhiteSpace(effectiveProxy))
            {
                AddLog("INFO", "Proxy",
                    "直连 VoWiFi 启动未成功；L3 看门狗将在下次尝试时重新核算出口代理路由（若存在匹配路由将自动转向 SOCKS5 重试整条链路）。");
            }

            // A manual OFF may race a start already inside the kernel lifecycle
            // gate. OFF wins deterministically and performs a second idempotent
            // stop after that older start unwinds.
            if (GetVoWifiIntentVersion(targetSlot.Id) != expectedIntentVersion)
            {
                await Kernel.StopVoWifiAsync(targetSlot.Id).ConfigureAwait(false);
                return false;
            }
            return started;
        }

        private long AdvanceVoWifiIntent(string slotId) =>
            _voWifiIntentVersions.AddOrUpdate(slotId, 1, static (_, current) => current + 1);

        private long GetVoWifiIntentVersion(string slotId) =>
            _voWifiIntentVersions.TryGetValue(slotId, out var version) ? version : 0;

        private static string GetVoWifiPreferenceKey(ModemSlot slot) =>
            $"{slot.Id}|{slot.Sim?.Iccid ?? "NO_SIM"}";

        /// <summary>
        /// True when the user explicitly stopped VoWiFi for this slot during
        /// the current session. The L3 watchdog respects this runtime veto
        /// while ignoring the stored DefaultVoWifi preference.
        /// </summary>
        internal bool IsVoWifiExplicitlyDisabled(ModemSlot slot) =>
            _voWifiDesiredStates.TryGetValue(GetVoWifiPreferenceKey(slot), out var desired) && !desired;

        public async Task<(bool Success, long RttMs, string Status)> ProbeVoWifiLivenessAsync(string? slotId = null)
        {
            var result = await Kernel.ProbeVoWifiLivenessAsync(slotId);
            AddLog(result.Success ? "INFO" : "WARN", "VoWiFi", $"SIP 探针保活检测: {(result.Success ? "成功" : "失败")}, RTT: {result.RttMs}ms, 状态: {result.Status}");
            return result;
        }

        public async Task<HostImsProbeResult> ProbeHostImsAsync(
            string? slotId = null,
            CancellationToken cancellationToken = default)
        {
            var slot = !string.IsNullOrWhiteSpace(slotId)
                ? Slots.FirstOrDefault(s => s.Id.Equals(slotId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"Host IMS slot '{slotId}' is unavailable.")
                : ActiveSlot ?? Slots.FirstOrDefault();

            if (slot == null)
                throw new InvalidOperationException("未发现可用蜂窝模组。");
            if (slot.IsPcscReader)
                throw new InvalidOperationException("PC/SC 读卡器没有蜂窝 IMS 数据面；请选择 AT 蜂窝模组。");
            if (slot.IsProfileSwitchInProgress)
                throw new InvalidOperationException("eSIM 正在切换配置，完成后再探测 Host IMS。");
            if (slot.Modem is not { IsOpen: true } modem)
                throw new InvalidOperationException("所选模组尚未连接，无法读取 IMS PDP Context。");

            AddLog("INFO", "IMS", $"正在只读探测卡槽 [{slot.Name}] 的蜂窝 Host IMS 前置条件。");
            var result = await modem.ProbeHostImsPdnAsync(cancellationToken).ConfigureAwait(false);
            AddLog(result.CanAttemptWindowsIms ? "INFO" : "WARN", "IMS",
                $"Host IMS 探测完成：{result.Readiness}；USB={result.UsbNetworkMode ?? "unknown"}；{result.Summary}");
            return result;
        }

        public HostImsRegistrationStatus GetHostImsRegistrationStatus(string? slotId = null) =>
            Kernel.GetHostImsRegistrationStatus(slotId);

        public async Task<HostImsRegistrationStatus> StartHostImsRegistrationAsync(
            string? slotId = null,
            HostImsEndpointCandidate? endpoint = null,
            CancellationToken cancellationToken = default)
        {
            AddLog("WARN", "IMS", "正在尝试实验性蜂窝 Host IMS 注册；仅支持 Windows 已持有的 IMS bearer，尚无蜂窝 ipsec-3gpp 数据面。");
            var status = await Kernel.StartHostImsRegistrationAsync(slotId, endpoint, cancellationToken).ConfigureAwait(false);
            AddLog(status.IsRegistered ? "INFO" : "WARN", "IMS", $"Host IMS 注册状态：{status.State}；{status.Endpoint ?? "无端点"}");
            return status;
        }

        public async Task StopHostImsRegistrationAsync(string? slotId = null)
        {
            await Kernel.StopHostImsRegistrationAsync(slotId).ConfigureAwait(false);
            AddLog("INFO", "IMS", "已停止实验性蜂窝 Host IMS 注册。");
        }

        public void OnUsbDeviceInserted()
        {
            AddLog("INFO", "USB Monitor", "检测到 USB 设备插入事件，等待串口驱动完成初始化...");

            _usbDebounceCts?.Cancel();
            _usbDebounceCts = new CancellationTokenSource();
            SafeFire.Run(HandleUsbDeviceInsertedAsync(_usbDebounceCts.Token));
        }

        public void OnUsbDeviceRemoved()
        {
            AddLog("INFO", "USB Monitor", "检测到 USB 设备拔出事件，正在保留 VoWiFi 会话并释放硬件句柄...");
            _usbDebounceCts?.Cancel();
            _usbDebounceCts = new CancellationTokenSource();
            SafeFire.Run(HandleUsbDeviceRemovedAsync(_usbDebounceCts.Token));
        }

        private async Task HandleUsbDeviceRemovedAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                var availablePorts = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var slot in Kernel.GetSlots().Where(slot => !availablePorts.Contains(slot.PortName)))
                {
                    await slot.DetachHardwareAsync(token).ConfigureAwait(false);
                    AddLog("INFO", "USB Monitor",
                        $"模块 [{slot.Name}] 已拔出；现有 VoWiFi 隧道将继续保活，重新插入后会自动重新注册。");
                }
                await RunOnUiAsync(SyncSlots, DispatcherPriority.DataBind).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                AddLog("ERROR", "USB Monitor", $"USB 拔出处理失败: {ex.Message}");
            }
        }

        private async Task HandleUsbDeviceInsertedAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(800, token).ConfigureAwait(false);
                AddLog("INFO", "USB Monitor", "开始扫描新插入设备提供的串口...");

                var discovered = await DiscoverSlotsCoreAsync(token).ConfigureAwait(false);
                await RunOnUiAsync(SyncSlots, DispatcherPriority.DataBind).ConfigureAwait(false);
                AddLog("INFO", "USB Monitor", $"USB 扫描完成，当前卡槽数: {discovered.Count}");

                foreach (var slot in discovered)
                {
                    await ApplyPreferencesToSlotAsync(slot).ConfigureAwait(false);
                }

                // 重插后自动刷新 eSIM Profile 列表：USB 扫描只发现槽位，不会触发 eUICC 探测，
                // 界面 Profile 列表依赖 EuiccProbeCompleted 事件驱动，必须显式探测一次。
                foreach (var slot in discovered)
                {
                    if (slot.IsPcscReader)
                        SafeFire.Run(ProbeEuiccAfterReinsertAsync(slot));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "USB Monitor", $"USB 设备扫描失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 重插后带短间隔重试的 eUICC 探测。读卡器重新枚举后 eUICC 可能尚未就绪，
        /// 首次探测可能失败；这里最多重试 3 次（间隔 1s/2s），成功后触发
        /// EuiccProbeCompleted 事件刷新界面 Profile 列表。
        /// </summary>
        private async Task ProbeEuiccAfterReinsertAsync(ModemSlot slot)
        {
            try
            {
                var delays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) };
                for (var attempt = 0; attempt <= delays.Length; attempt++)
                {
                    if (attempt > 0)
                        await Task.Delay(delays[attempt - 1]).ConfigureAwait(false);

                    if (slot.IsProfileSwitchInProgress) return;

                    var result = await ProbeEuiccAsync(slot.Id).ConfigureAwait(false);
                    EuiccProbeCompleted?.Invoke(slot.Id, result);
                    if (result.Capability == EuiccCapability.Supported && result.Profiles.Count > 0)
                        return;
                }
            }
            catch (Exception ex)
            {
                AddLog("WARN", "eSIM", $"重插后 eUICC 探测失败（{slot.Name}）：{ex.Message}");
            }
        }

        public Task ApplyPreferencesToSlotAsync(ModemSlot slot) =>
            ApplyPreferencesToSlotAsync(slot, restoreAutoVoWifi: true);

        /// <summary>
        /// Restores (and, on first sight, persists) the automatic display name of a
        /// card. The name is keyed by ICCID so the same card keeps the same label
        /// across profile switches and restarts:
        ///   1. the MSISDN read via AT+CNUM,
        ///   2. the subscriber number IMS handed out in P-Associated-URI,
        ///   3. a fallback built from the operator name and the ICCID tail.
        /// A user-supplied nickname always wins over the automatic name.
        /// </summary>
        private async Task RestoreAutoDisplayNameAsync(ModemSlot slot, SimPreferenceModel? simPref)
        {
            var sim = slot.Sim;
            if (sim == null || string.IsNullOrWhiteSpace(sim.Iccid)) return;

            var iccid = sim.Iccid.Trim();
            var associatedUri = slot.VoWifiDiag?.Ims?.PAssociatedUri;
            var operatorName = CarrierDisplayHelper.GetOperatorBrand(sim.Mcc, sim.Mnc, sim.OperatorName, iccid);

            var resolved = CardDisplayNameResolver.ResolveAutoDisplayName(sim, associatedUri, iccid, operatorName);
            if (string.IsNullOrWhiteSpace(resolved)) return;

            var saved = simPref?.DisplayName;

            // Persist the automatic name under the ICCID so the label survives
            // a later switch, even when IMS was the only source that had it.
            if (!string.Equals(saved, resolved, StringComparison.Ordinal))
            {
                var record = simPref ?? new SimPreferenceModel { Iccid = iccid };
                record.DisplayName = resolved;
                record.Imsi = sim.Imsi;
                try
                {
                    await Preferences.SaveSimPreferenceAsync(record).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AddLog("WARN", "Preferences", $"保存卡片显示名失败: {ex.Message}");
                }
            }

            // Nickname wins; then the persisted or just-resolved auto name.
            slot.AutoDisplayName = CardDisplayNameResolver.ResolveEffectiveName(
                simPref?.CardNickname,
                !string.IsNullOrWhiteSpace(saved) ? saved : resolved,
                sim,
                associatedUri);
        }

        private async Task ApplyPreferencesToSlotAsync(
            ModemSlot slot,
            bool restoreAutoVoWifi,
            bool restoreRadioPreferences = true)
        {
            try
            {
                var modPref = await Preferences.GetModulePreferenceAsync(slot.Id, slot.Imei);
                SimPreferenceModel? simPref = null;
                if (slot.Sim != null && !string.IsNullOrEmpty(slot.Sim.Iccid))
                {
                    simPref = await Preferences.GetSimPreferenceAsync(slot.Sim.Iccid);
                }

                if (modPref != null && !string.IsNullOrWhiteSpace(modPref.CustomName))
                {
                    slot.Name = modPref.CustomName.Trim();
                }
                if (slot.IsPcscReader && modPref?.Imei is { Length: > 0 } configuredImei)
                {
                    // ModulePreferences.Imei historically stores the modem's
                    // AT+CGSN value. For a PC/SC-only slot it stores the user
                    // supplied genuine terminal identity used by IMS.
                    slot.VoWifiImei = configuredImei;
                }
                if (modPref?.DeviceProfile is { } dp)
                {
                    slot.VoWifiDeviceProfile = dp;
                    if (slot.VoWifi is not null) slot.VoWifi.DeviceProfile = dp;
                    // Auto-generate IMEI once on first load so the identity stays stable.
                    if (slot.IsPcscReader && string.IsNullOrWhiteSpace(slot.VoWifiImei))
                    {
                        dp.Imei ??= ImeiGenerator.Generate(dp.Tac);
                        slot.VoWifiImei = dp.Imei;
                    }
                }
                slot.CardNickname = simPref?.CardNickname;
                await RestoreAutoDisplayNameAsync(slot, simPref).ConfigureAwait(false);

                // Routing is resolved from exactly two persistent tiers:
                // ICCID card rule first, then IMSI MCC/PLMN country rule.
                // Legacy module/default proxy fields are deliberately ignored
                // here so they cannot silently override the visible rule table.
                var resolvedProxy = ResolveEgressProxyForSlot(slot.Id);
                if (!Kernel.SetSlotProxy(slot.Id, resolvedProxy))
                {
                    Kernel.SetSlotProxy(slot.Id, null);
                    slot.ProxyUrl = null;
                    AddLog("WARN", "Proxy",
                        $"卡槽 [{slot.Name}] 命中的分流代理无效，已安全回退到直连；VoWiFi 仅允许 socks://、socks5:// 或 socks5h://。");
                }
                else
                {
                    slot.ProxyUrl = resolvedProxy;
                    AddLog("INFO", "Proxy", resolvedProxy is null
                        ? $"已按 ICCID > IMSI MCC/PLMN 规则解析卡槽 [{slot.Name}]：直连。"
                        : $"已按 ICCID > IMSI MCC/PLMN 规则解析卡槽 [{slot.Name}]：{DescribeProxyEndpoint(resolvedProxy)}");
                }

                slot.CustomEpdg = simPref?.CustomEpdg;
                ApplyVoWifiHomeIdentity(slot, simPref);

                // A SIM preference follows the card and overrides the module
                // default. Restore RF state before starting any background work
                // that could report cellular registration or signal state.
                // Radio/data/VoWiFi switches belong to the SIM, never to the
                // physical slot.  A SIM without a saved record starts with all
                // switches off so moving it to another modem is predictable.
                bool? preferredFlightMode = simPref?.DefaultFlightMode ?? false;
                if (restoreRadioPreferences && !slot.IsPcscReader && preferredFlightMode.HasValue && slot.IsFlightMode != preferredFlightMode.Value)
                {
                    try
                    {
                        var applied = await slot.SetFlightModeAsync(preferredFlightMode.Value).ConfigureAwait(false);
                        if (!applied)
                        {
                            AddLog("WARN", "Preferences", $"无法为卡槽 [{slot.Name}] 恢复飞行模式偏好。");
                        }
                        else
                        {
                            AddLog("INFO", "Preferences", preferredFlightMode.Value
                                ? $"已为卡槽 [{slot.Name}] 恢复飞行模式，蜂窝射频已关闭。"
                                : $"已为卡槽 [{slot.Name}] 恢复正常模式，蜂窝射频已开启。");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog("WARN", "Preferences", $"恢复飞行模式偏好失败，继续处理其他设置: {ex.Message}");
                    }
                }

                if (restoreRadioPreferences && !slot.IsPcscReader && !slot.IsFlightMode)
                {
                    bool? preferredRoaming = simPref?.DefaultDataRoaming ?? false;
                    if (preferredRoaming.HasValue)
                    {
                        try
                        {
                            var applied = await slot.SetDataRoamingEnabledAsync(preferredRoaming.Value).ConfigureAwait(false);
                            AddLog(applied ? "INFO" : "WARN", "Preferences", applied
                                ? $"已为卡槽 [{slot.Name}] 恢复数据漫游偏好: {(preferredRoaming.Value ? "允许" : "禁止")}。"
                                : $"模组未确认数据漫游偏好，可能不支持 Quectel 漫游指令。");
                        }
                        catch (Exception ex)
                        {
                            AddLog("WARN", "Preferences", $"恢复数据漫游偏好失败，继续处理其他设置: {ex.Message}");
                        }
                    }

                    bool? preferredCellularData = simPref?.DefaultCellularData ?? false;
                    if (preferredCellularData.HasValue)
                    {
                        try
                        {
                            var applied = await slot.SetCellularDataEnabledAsync(preferredCellularData.Value).ConfigureAwait(false);
                            AddLog(applied ? "INFO" : "WARN", "Preferences", applied
                                ? $"已为卡槽 [{slot.Name}] 恢复蜂窝数据偏好: {(preferredCellularData.Value ? "开启" : "关闭")}。"
                                : $"模组未确认蜂窝数据偏好 (CGATT)。");
                        }
                        catch (Exception ex)
                        {
                            AddLog("WARN", "Preferences", $"恢复蜂窝数据偏好失败，继续处理 VoWiFi: {ex.Message}");
                        }
                    }

                    try { await slot.RefreshMetricsAsync().ConfigureAwait(false); }
                    catch (Exception ex) { AddLog("WARN", "Preferences", $"偏好恢复后的网络刷新失败: {ex.Message}"); }
                }

                if (restoreAutoVoWifi)
                {
                    var autoVoWifi = simPref?.DefaultVoWifi ?? false;
                    if (autoVoWifi)
                    {
                        QueueAutoVoWifiStart(slot);
                    }
                    else
                    {
                        _voWifiDesiredStates[GetVoWifiPreferenceKey(slot)] = false;
                        AdvanceVoWifiIntent(slot.Id);
                        _autoVoWifiStarts.TryRemove(slot.Id, out _);
                        if (slot.VoWifi.State != VoWifiState.Disconnected)
                        {
                            await slot.StopVoWifiAsync().ConfigureAwait(false);
                            AddLog("INFO", "VoWiFi", $"已按卡槽 [{slot.Name}] 的关闭偏好停止自动 VoWiFi。");
                        }
                    }
                }

                SafeFire.Run(SyncModemSmsAsync(slot));
            }
            catch (Exception ex)
            {
                AddLog("WARN", "Preferences", $"Apply preferences failed: {ex.Message}");
            }
        }

        private static string DescribeProxyEndpoint(string proxyUrl)
        {
            if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri))
                return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            return proxyUrl;
        }

        private void ApplyVoWifiHomeIdentity(ModemSlot slot, SimPreferenceModel? simPref)
        {
            var reported = slot.Sim;
            var savedImsi = simPref?.Imsi?.Trim();
            if (reported == null || string.IsNullOrWhiteSpace(savedImsi) ||
                savedImsi.Equals(reported.Imsi, StringComparison.Ordinal))
            {
                return;
            }

            // The preference record is selected by the live ICCID. Preserve a
            // previously observed permanent IMSI for a multi-IMSI profile without
            // consulting a bundled carrier/ICCID table.
            if (savedImsi.Length is < 5 or > 16 || !savedImsi.All(char.IsAsciiDigit))
                return;
            var learnedIdentity = SimIdentity.FromImsiAndIccid(
                savedImsi,
                reported.Iccid,
                reported.OperatorName,
                reported.PhoneNumber,
                reported.IsHomePlmnAuthoritative ? reported.Mnc.Length : null,
                reported.HomePlmns);
            slot.RememberVoWifiIdentity(learnedIdentity);
            AddLog("INFO", "VoWiFi", $"检测到临时 IMSI，已为卡槽 [{slot.Name}] 恢复已验证的归属网络 EAP-AKA 身份。");
        }

        private void QueueAutoVoWifiStart(ModemSlot slot)
        {
            var preferenceKey = GetVoWifiPreferenceKey(slot);
            if (_voWifiDesiredStates.TryGetValue(preferenceKey, out var desired) && !desired) return;

            // 自动 VoWiFi 只在卡身份 READY 后触发，避免启动早期卡尚未就绪时
            // 立即排队注册必然抛 WARN 的“先报错”观感。卡后来就绪会由 L3
            // 看门狗按节奏恢复。
            if (slot.Sim == null) return;
            _voWifiDesiredStates.TryAdd(preferenceKey, true);
            if (slot.VoWifi.State is VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka or
                VoWifiState.IpsecTunnelEstablished or VoWifiState.ImsRegistering or VoWifiState.ImsRegistered)
                return;
            if (!_autoVoWifiStarts.TryAdd(slot.Id, 0)) return;
            var expectedIntentVersion = GetVoWifiIntentVersion(slot.Id);

            _ = Task.Run(async () =>
            {
                try
                {
                    if (GetVoWifiIntentVersion(slot.Id) != expectedIntentVersion ||
                        (_voWifiDesiredStates.TryGetValue(preferenceKey, out var stillDesired) && !stillDesired)) return;
                    AddLog("INFO", "VoWiFi", $"卡槽 [{slot.Name}] 已启用自动 VoWiFi，正在注册…");
                    var started = await StartVoWifiCoreAsync(slot, expectedIntentVersion).ConfigureAwait(false);
                    if (!started)
                        AddLog("WARN", "VoWiFi", $"卡槽 [{slot.Name}] 自动 VoWiFi 注册失败，请查看 VoWiFi 日志。 ");
                }
                catch (Exception ex)
                {
                    AddLog("WARN", "VoWiFi", $"卡槽 [{slot.Name}] 自动 VoWiFi 启动异常: {ex.Message}");
                }
                finally
                {
                    _autoVoWifiStarts.TryRemove(slot.Id, out _);
                }
            });
        }

        public async Task SyncModemSmsAsync(ModemSlot slot, CancellationToken cancellationToken = default)
        {
            if (slot.IsPcscReader)
            {
                // Inbound/outbound SMS uses the IMS SIP path for a PC/SC slot;
                // there is no modem message store to enumerate.
                return;
            }
            if (slot?.Sms == null) return;
            if (!_syncingSlots.TryAdd(slot.Id, 0)) return;

            AddLog("INFO", "SMS", $"Starting SMS synchronization for slot [{slot.Name}] ({slot.Id})...");
            try
            {
                // 1. Fetch all SMS stored on the module (both SIM and module memory)
                var messages = await slot.Sms.ListSmsAsync(SmsStatus.All, cancellationToken).ConfigureAwait(false);
                AddLog("INFO", "SMS", $"Found {messages.Count} stored messages on modem [{slot.Name}].");

                int importedCount = 0;
                foreach (var msg in messages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    bool isOutgoing = msg.Direction == SmsDirection.Submitted;
                    var remote = !string.IsNullOrWhiteSpace(msg.SenderOrRecipient) ? msg.SenderOrRecipient : "Unknown";
                    var normalizedTimestamp = isOutgoing
                        ? TimestampDisplayHelper.ToUtcStorageTime(msg.Timestamp)
                        : TimestampDisplayHelper.NormalizeIncomingNetworkTime(msg.Timestamp);

                    bool exists = await Preferences.HasSmsMessageAsync(remote, normalizedTimestamp, msg.Text, msg.RawPdu).ConfigureAwait(false);
                    if (!exists)
                    {
                        var model = new SmsMessageModel
                        {
                            Index = msg.Index,
                            SlotId = slot.Id,
                            SenderOrRecipient = remote,
                            Text = msg.Text,
                            Timestamp = normalizedTimestamp,
                            IsOutgoing = isOutgoing,
                            DeliveryState = isOutgoing ? SmsDeliveryState.Sent : SmsDeliveryState.Received,
                            DeliveryStatus = msg.DeliveryStatus,
                            MessageReference = msg.MessageReference,
                            RawPdu = msg.RawPdu
                        };

                        await Preferences.SaveSmsMessageAsync(model).ConfigureAwait(false);

                        await RunOnUiAsync(() =>
                        {
                            var conv = GetOrCreateConversation(remote);
                            if (!conv.Messages.Any(m => m.Timestamp == model.Timestamp && m.Text == model.Text))
                            {
                                conv.Messages.Add(model);
                                if (!model.IsOutgoing)
                                {
                                    conv.UnreadCount++;
                                }
                                conv.UpdateLastMessage();
                                MoveConversationToTop(conv);
                            }
                        }).ConfigureAwait(false);

                        importedCount++;
                    }
                }

                if (importedCount > 0)
                {
                    AddLog("INFO", "SMS", $"Imported {importedCount} new messages from modem [{slot.Name}] into local SQLite.");
                }

                // 2. Delete all messages from modem storages to prevent card storage from filling up
                if (slot.Modem != null && slot.Modem.IsOpen)
                {
                    var storages = new[] { "\"SM\",\"SM\",\"SM\"", "\"ME\",\"ME\",\"ME\"" };
                    foreach (var st in storages)
                    {
                        try
                        {
                            await slot.Modem.SendRawAtCommandAsync($"AT+CPMS={st}", 2000, cancellationToken).ConfigureAwait(false);
                            var delResp = await slot.Modem.SendRawAtCommandAsync("AT+CMGD=1,4", 5000, cancellationToken).ConfigureAwait(false);
                            if (!delResp.Success)
                            {
                                foreach (var m in messages)
                                {
                                    if (m.Index > 0)
                                    {
                                        await slot.Modem.SendRawAtCommandAsync($"AT+CMGD={m.Index}", 2000, cancellationToken).ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AddLog("WARN", "SMS", $"Clear storage {st} failed: {ex.Message}");
                        }
                    }

                    // Restore preferred storage to ME
                    try
                    {
                        await slot.Modem.SendRawAtCommandAsync("AT+CPMS=\"ME\",\"ME\",\"ME\"", 2000, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AddLog("WARN", "SMS", $"Restore SMS storage to ME failed on [{slot.Name}]: {ex.Message}");
                    }

                    AddLog("INFO", "SMS", $"Modem [{slot.Name}] SMS storage cleaned up.");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AddLog("ERROR", "SMS", $"Modem SMS sync failed for [{slot.Name}]: {ex.Message}");
            }
            finally
            {
                _syncingSlots.TryRemove(slot.Id, out _);
            }
        }

        public async Task SaveModulePreferencesAsync(string slotId, bool flightMode, bool vowifi, bool cellularData, bool roaming, string? proxyUrl, string? customName = null, VoSharp.Sim.DeviceProfile? deviceProfile = null)
        {
            var slot = Kernel.GetSlots().FirstOrDefault(s => s.Id == slotId);
            var normalizedName = string.IsNullOrWhiteSpace(customName) ? null : customName.Trim();
            var model = new ModulePreferenceModel
            {
                Id = slotId,
                Imei = slot?.IsPcscReader == true ? slot.VoWifiImei : slot?.Imei,
                DeviceProfile = deviceProfile ?? slot?.VoWifiDeviceProfile,
                PortName = slot?.PortName,
                CustomName = normalizedName,
                DefaultFlightMode = flightMode,
                DefaultVoWifi = vowifi,
                DefaultCellularData = cellularData,
                DefaultDataRoaming = roaming,
                DefaultProxyUrl = proxyUrl,
                BaudRate = slot?.BaudRate ?? 115200,
                LastSeenAt = DateTime.Now
            };
            await Preferences.SaveModulePreferenceAsync(model);
            if (slot != null)
            {
                slot.Name = normalizedName ?? $"Slot {slot.Id} ({slot.PortName})";
            }
            AddLog("INFO", "Preferences", $"Saved module preference for [{slotId}]");
        }

        private async Task PersistRuntimeIntentAsync(
            ModemSlot slot,
            bool? flightMode = null,
            bool? voWifi = null)
        {
            var sim = slot.Sim;
            if (sim == null || string.IsNullOrWhiteSpace(sim.Iccid)) return;

            await _runtimePreferenceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var existing = await Preferences.GetSimPreferenceAsync(sim.Iccid).ConfigureAwait(false)
                    ?? new SimPreferenceModel { Iccid = sim.Iccid };
                existing.Imsi = sim.Imsi;
                existing.DefaultFlightMode = flightMode ?? existing.DefaultFlightMode;
                existing.DefaultVoWifi = voWifi ?? existing.DefaultVoWifi;
                existing.LastSeenAt = DateTime.Now;
                await Preferences.SaveSimPreferenceAsync(existing).ConfigureAwait(false);
            }
            finally
            {
                _runtimePreferenceGate.Release();
            }
        }

        public async Task SaveSimPreferencesAsync(string iccid, bool flightMode, bool vowifi, bool cellularData, bool roaming, string? proxyUrl, string? nickname, string? customEpdg = null)
        {
            var normalizedNickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
            var normalizedEpdg = string.IsNullOrWhiteSpace(customEpdg) ? null : customEpdg.Trim();
            var slot = Kernel.GetSlots().FirstOrDefault(s =>
                string.Equals(s.Sim?.Iccid, iccid, StringComparison.Ordinal));
            await _runtimePreferenceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var model = await Preferences.GetSimPreferenceAsync(iccid).ConfigureAwait(false)
                    ?? new SimPreferenceModel { Iccid = iccid };
                model.Imsi = slot?.Sim?.Imsi ?? model.Imsi;
                model.CardNickname = normalizedNickname;
                model.DefaultFlightMode = flightMode;
                model.DefaultVoWifi = vowifi;
                model.DefaultCellularData = cellularData;
                model.DefaultDataRoaming = roaming;
                model.DedicatedProxyUrl = proxyUrl;
                model.CustomEpdg = normalizedEpdg;
                model.LastSeenAt = DateTime.Now;
                await Preferences.SaveSimPreferenceAsync(model).ConfigureAwait(false);
            }
            finally
            {
                _runtimePreferenceGate.Release();
            }
            if (slot != null)
            {
                slot.CardNickname = normalizedNickname;
                slot.CustomEpdg = normalizedEpdg;
            }
            AddLog("INFO", "Preferences", $"Saved sim preference for [{iccid}]");
        }

        private int _serviceDisposed;
        public async ValueTask DisposeAsync()
        {
        Diag?.LogCheckpoint("VoKernelService.DisposeAsync 开始");

            if (Interlocked.Exchange(ref _serviceDisposed, 1) != 0) return;

            var audioCts = Interlocked.Exchange(ref _cellularCallAudioCts, null);
            audioCts?.Cancel();
            audioCts?.Dispose();
            await _desktopAudioTransition.WaitAsync().ConfigureAwait(false);
            try
            {
                StopMicrophone();
                await StopCellularAudioBridgeAsync().ConfigureAwait(false);
                await StopQdc507VoiceRouteAsync().ConfigureAwait(false);
            }
            finally { _desktopAudioTransition.Release(); }
            await _cellularAudioBridge.DisposeAsync();

            try
            {
                _usbDebounceCts?.Cancel();
            }
            catch { }
            try
            {
                _usbDebounceCts?.Dispose();
            }
            catch { }

            _logLines.Writer.TryComplete();
            try
            {
                await _logWriterTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                _logWriterCts.Cancel();
            }
            _logWriterCts.Dispose();

            await StopWatchdogsAsync();

            try
            {
                await Kernel.DisposeAsync();
            }
            catch (Exception ex)
            {
                AddLog("WARN", "VoKernelService", $"Kernel dispose fault: {ex.Message}");
            }
        }
    }
}

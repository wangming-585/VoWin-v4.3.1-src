using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoSharp.Common.Utils;
using VoSharp.Euicc.Models;
using VoSharp.Kernel.Pool;
using VoSharp.Sim;
using VoSharp.StateMachine;
using VoSharp.Telephony.VoWifi;
using VoWin.Helpers;
using VoWin.Services;
using static VoWin.Helpers.VoWifiStatePresenter;

namespace VoWin.ViewModels.Pages;

/// <summary>
/// Home dashboard: the user-facing front page. Everything shown here is
/// derived from the live kernel service — no cached or synthetic state. The
/// nine-step VoWiFi presentation, the current-card panel and the eSIM profile
/// summary are all read-only views over the same backend the advanced pages
/// use, so this page can never disagree with the engine.
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly IVoKernelService _kernelService;
    private bool _userStopped;
    private int _heartbeatCount;
    private readonly DispatcherTimer? _uptimeTimer;

    public DashboardViewModel(IVoKernelService kernelService)
    {
        _kernelService = kernelService;

        Slots.CollectionChanged += (_, _) =>
        {
            if (SelectedSlot == null || !Slots.Contains(SelectedSlot))
                SelectedSlot = _kernelService.ActiveSlot ?? Slots.FirstOrDefault();
            OnPropertyChanged(string.Empty);
        };

        void ScheduleRefresh()
        {
            App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(string.Empty),
                DispatcherPriority.DataBind);
        }

        _kernelService.Kernel.TelephonyStateChanged += (_, _) => ScheduleRefresh();
        _kernelService.Kernel.VoWifiStateChanged += (_, _) => ScheduleRefresh();
        _kernelService.Kernel.SignalQualityChanged += (_, _) => ScheduleRefresh();
        _kernelService.Kernel.NetworkRegistrationChanged += (_, _) => ScheduleRefresh();
        _kernelService.Kernel.SimStateChanged += (_, _) => ScheduleRefresh();
        _kernelService.EuiccProbeCompleted += OnEuiccProbeCompleted;

        SelectedSlot = _kernelService.ActiveSlot ?? Slots.FirstOrDefault();

        // 已连接状态后引擎不再发状态事件，Uptime 需要本地心跳驱动走秒。
        // 同时，信号强度/注册/网络状态也属于实时展示项，引擎只在值变化时
        // 推送事件；此处复用秒级心跳同步刷新，避免连接期间停在旧快照。
        _uptimeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _heartbeatCount = 0;
        _uptimeTimer.Tick += (_, _) =>
        {
            // 自愈：读卡器在位但槽位被误判为“未插卡”时，重新附着一次读卡器
            // 并重读卡身份。等价于设备管理页的“重载 SIM”，但无需用户手动切页。
            if (!SimReady && _heartbeatCount % 12 == 0)
            {
                var slot = SelectedSlot;
                if (slot is not null && slot.IsPcscReader)
                {
                    SafeFire.Run(async () =>
                    {
                        try
                        {
                            // 重新扫描会把在位读卡器重新附着并读回卡身份，
                            // 等价于设备管理页的“重载 SIM”，但无需用户手动切页。
                            await _kernelService.DiscoverSlotsAsync();
                        }
                        catch { /* 读卡器暂不可用则等下一轮 */ }
                    });
                }
            }

            if (IsConnected) OnPropertyChanged(nameof(UptimeDisplay));
            OnPropertyChanged(nameof(SignalDisplay));
            OnPropertyChanged(nameof(RegistrationDisplay));
            OnPropertyChanged(nameof(TelephonyStateDisplay));
            OnPropertyChanged(nameof(VoWifiAvailabilityDisplay));

            // 每 5 秒从模组拉一次真实信号/注册值。连接稳定后引擎不再推事件，
            // 仅重绘只会重复旧快照；必须主动轮询底层才有新数据。
            _heartbeatCount++;
            if (_heartbeatCount % 5 == 0)
            {
                var slotId = SelectedSlot?.Id;
                SafeFire.Run(async () =>
                {
                    await _kernelService.RefreshMetricsAsync(slotId).ConfigureAwait(true);
                    await App.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        OnPropertyChanged(nameof(SignalDisplay));
                        OnPropertyChanged(nameof(RegistrationDisplay));
                    }, DispatcherPriority.DataBind)!;
                });
            }
        };
        _uptimeTimer.Start();

        // Pull the profile list once at startup so the eSIM panel is populated
        // without requiring the user to visit the device manager first.
        SafeFire.Run(LoadProfilesQuietlyAsync());
    }

    public ObservableCollection<ModemSlot> Slots => _kernelService.Slots;

    [ObservableProperty]
    private ModemSlot? _selectedSlot;

    partial void OnSelectedSlotChanged(ModemSlot? value) => OnPropertyChanged(string.Empty);

    // ================= A. VoWiFi connection status =================

    public VoWifiDiagnosticInfo? VoWifiDiag => ResolveDiag();

    /// <summary>
    /// 统一槽位数据源：选中槽位优先（显式传槽位 ID，避免 Kernel 内部回退到
    /// ActiveSlot）；仅在没有选中槽位时才退回 Kernel 默认诊断。否则状态标题、
    /// 连接时间、SIM 判断会各自指向不同槽位，出现状态与时间不匹配。
    /// </summary>
    private VoWifiDiagnosticInfo? ResolveDiag()
    {
        if (SelectedSlot is { } slot)
        {
            return slot.VoWifiDiag
                ?? _kernelService.Kernel.GetVoWifiDiagnostics(slot.Id);
        }
        return _kernelService.VoWifiDiag;
    }

    public VoWifiState VoWifiState => VoWifiDiag?.State ?? VoWifiState.Disconnected;

    private bool AnySlotPresent => Slots.Count > 0;
    private bool SimReady => !string.IsNullOrWhiteSpace(CurrentSim?.Iccid);

    /// <summary>True after an explicit user stop, cleared when a new start begins.</summary>
    private bool UserStopped
    {
        get => _userStopped;
        set
        {
            if (_userStopped != value)
            {
                _userStopped = value;
                OnPropertyChanged(string.Empty);
            }
        }
    }

    public DashboardPhase Phase => ResolvePhase(VoWifiState, AnySlotPresent, SimReady, UserStopped);

    public Presentation CurrentPresentation => Present(Phase);

    public string StatusTitle => CurrentPresentation.Title;
    public string StatusDescription => CurrentPresentation.Description;
    public string StatusTone => CurrentPresentation.Tone;

    public bool IsConnected => Phase == DashboardPhase.Connected;
    public bool IsFailed => Phase == DashboardPhase.Failed;
    public bool IsBusyPhase => CurrentPresentation.IsBusy;

    /// <summary>Which of the four progress steps is currently in flight (1-4, 0 = none).</summary>
    public int ProgressStep => Phase switch
    {
        DashboardPhase.ResolvingEpdg => 1,
        DashboardPhase.Authenticating => 2,
        DashboardPhase.BuildingTunnel => 3,
        DashboardPhase.RegisteringIms => 4,
        DashboardPhase.Connected => 4,
        _ => 0
    };

    public bool CanStartVoWifi => !IsBusyPhase && !IsBusy && SimReady && !IsConnected;
    public bool CanStopVoWifi => !IsBusy && (IsConnected || IsBusyPhase);
    public bool CanReconnectVoWifi => !IsBusy && SimReady;

    public string FailureReason => IsFailed
        ? DescribeFailure(VoWifiDiag?.FailureStage, VoWifiDiag?.FailureCategory, VoWifiDiag?.LastError)
        : string.Empty;

    /// <summary>
    /// 已连接时间：基于引擎 <see cref="VoWifiDiagnosticInfo.ConnectedAt"/> 实时计算。
    /// 引擎的 Uptime 字段只是状态事件时刻的快照，连接成功后不再有状态事件，
    /// 界面会冻结在 00:00:00；由 <see cref="_uptimeTimer"/> 每秒驱动本属性刷新。
    /// </summary>
    public string UptimeDisplay => FormatUptime(VoWifiDiag?.ConnectedAt, IsConnected);

    internal static string FormatUptime(DateTime? connectedAtUtc, bool isConnected)
    {
        if (!isConnected || connectedAtUtc is not { } connected) return string.Empty;
        var elapsed = DateTime.UtcNow - connected;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var clock = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        return elapsed.TotalDays >= 1
            ? $"已保持连接 {(int)elapsed.TotalDays} 天 {clock}"
            : $"已保持连接 {clock}";
    }

    // ================= B. Current SIM / eSIM card =================

    public SimIdentity? CurrentSim => SelectedSlot?.Sim ?? _kernelService.CurrentSim;

    public bool HasCurrentSim => !string.IsNullOrWhiteSpace(CurrentSim?.Iccid);

    public string CardTitle
    {
        get
        {
            if (!HasCurrentSim) return "未检测到 SIM 卡";
            var nickname = SelectedSlot?.CardNickname ?? SelectedSlot?.AutoDisplayName;
            return !string.IsNullOrWhiteSpace(nickname) ? nickname! : CarrierDisplay;
        }
    }

    public string SlotDisplay
    {
        get
        {
            if (SelectedSlot == null) return "无可用设备";
            var name = SelectedSlot.Name;
            var port = SelectedSlot.PortName;
            // PC/SC reader slots duplicate the reader name into both fields;
            // showing "SCR Prime 0（PC/SC · SCR Prime 0）" would be noise.
            // Equivalent when identical or when the port already embeds the
            // reader name ("SCR Prime 0" vs "PC/SC · SCR Prime 0").
            var duplicated = string.Equals(name, port, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(port) && port.Contains(name, StringComparison.OrdinalIgnoreCase));
            return duplicated ? port : $"{name}（{port}）";
        }
    }

    /// <summary>归属运营商：卡的发卡方，来自卡面/Profile 元数据。</summary>
    public string CarrierDisplay => HasCurrentSim
        ? CarrierDisplayHelper.GetOperatorDisplay(CurrentSim)
        : "未知";

    /// <summary>IMSI 认证网络：实际用于网络鉴权的 PLMN，可能与归属运营商不同（多 IMSI/漫游卡）。</summary>
    public string ImsiNetworkDisplay
    {
        get
        {
            if (!HasCurrentSim) return "未知";
            var sim = CurrentSim!;
            var plmn = !string.IsNullOrWhiteSpace(sim.Mcc) ? $"{sim.Mcc}-{sim.Mnc}" : null;
            var carrier = PlmnCarrierTable.Resolve(sim.Mcc ?? "", sim.Mnc ?? "");
            var country = MccCountryHelper.FindByMcc(sim.Mcc);
            var parts = new List<string>();
            if (country != null) parts.Add($"{country.Flag} {country.Name}");
            if (plmn != null) parts.Add($"PLMN {plmn}");
            if (!string.IsNullOrWhiteSpace(carrier)) parts.Add(carrier!);
            return parts.Count > 0 ? string.Join(" · ", parts) : "未知";
        }
    }

    public bool CarrierDiffersFromImsiNetwork
    {
        get
        {
            if (!HasCurrentSim) return false;
            var sim = CurrentSim!;
            var imsiCarrier = PlmnCarrierTable.Resolve(sim.Mcc ?? "", sim.Mnc ?? "");
            return !string.IsNullOrWhiteSpace(sim.OperatorName)
                && !string.IsNullOrWhiteSpace(imsiCarrier)
                && !sim.OperatorName.Contains(imsiCarrier!, StringComparison.OrdinalIgnoreCase)
                && !imsiCarrier!.Contains(sim.OperatorName, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 本机号码：优先读卡 MSISDN；读卡器不下发号码（PC/SC 常见）时，
    /// 兜底到 ICCID 路由表已保存的号码（用户曾手动维护或历史注册获得）。
    /// </summary>
    public string PhoneNumberDisplay
    {
        get
        {
            if (!HasCurrentSim) return "暂不可用";
            var live = CurrentSim!.PhoneNumber;
            if (!string.IsNullOrWhiteSpace(live)) return live!.Trim();

            var iccid = CurrentSim!.Iccid;
            if (!string.IsNullOrWhiteSpace(iccid))
            {
                var exact = _kernelService.IccidRoutes.FirstOrDefault(r =>
                    string.Equals(r.Iccid, iccid, StringComparison.OrdinalIgnoreCase));
                if (exact != null && !string.IsNullOrWhiteSpace(exact.PhoneNumber))
                    return exact.PhoneNumber!.Trim();
                var tail = CardDisplayNameResolver.IccidTail(iccid);
                if (tail.Length >= 4)
                {
                    var tails = _kernelService.IccidRoutes
                        .Where(r => !string.IsNullOrWhiteSpace(r.Iccid)
                            && r.Iccid.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (tails.Count == 1 && !string.IsNullOrWhiteSpace(tails[0].PhoneNumber))
                        return tails[0].PhoneNumber!.Trim();
                }
            }
            return "未知";
        }
    }

    public string IccidDisplay => HasCurrentSim ? CurrentSim!.Iccid : "—";
    public string EidDisplay => EuiccEid ?? "未知";

    public string SignalDisplay
    {
        get
        {
            var signal = SelectedSlot?.Signal ?? _kernelService.CurrentSignal;
            if (signal == null) return "未知";
            return $"{signal.Bars}/5 格 · {signal.RssiDbm} dBm";
        }
    }

    public string RegistrationDisplay
    {
        get
        {
            var reg = SelectedSlot?.Registration ?? _kernelService.CurrentRegistration;
            if (reg == null) return VoWifiState != VoWifiState.Disconnected ? "VoWiFi" : "未知";
            return reg.StatusDisplay;
        }
    }

    public string TelephonyStateDisplay =>
        DescribeTelephonyState(_kernelService.StateMachineState);

    public string VoWifiAvailabilityDisplay => IsConnected
        ? "VoWiFi 可用（语音/短信走 WiFi）"
        : SimReady ? "未连接（可发起 VoWiFi）" : "需先就绪 SIM 卡";

    // ================= C. eSIM profile management =================

    [ObservableProperty]
    private ObservableCollection<Profile> _profiles = [];

    [ObservableProperty]
    private string? _euiccEid;

    [ObservableProperty]
    private bool _isLoadingProfiles;

    [ObservableProperty]
    private string _profileStatusText = "尚未读取 eSIM 信息";

    public bool HasProfiles => Profiles.Count > 0;

    public Profile? EnabledProfile => Profiles.FirstOrDefault(p => p.IsEnabledState);

    private void OnEuiccProbeCompleted(string slotId, EuiccProbeResult result)
    {
        if (SelectedSlot != null && !string.Equals(slotId, SelectedSlot.Id, StringComparison.OrdinalIgnoreCase))
            return;
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(result.Eid)) EuiccEid = result.Eid;
            if (result.Profiles.Count > 0)
            {
                Profiles = new ObservableCollection<Profile>(result.Profiles);
                ProfileStatusText = $"共 {result.Profiles.Count} 个 Profile";
            }
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(EnabledProfile));
        });
    }

    [RelayCommand]
    private async Task LoadProfilesAsync()
    {
        if (IsLoadingProfiles) return;
        IsLoadingProfiles = true;
        ProfileStatusText = "正在读取 eSIM 信息…";
        try
        {
            var slot = SelectedSlot;
            EuiccEid = await _kernelService.GetEuiccEidAsync(slot?.Id);
            var profiles = await _kernelService.GetEuiccProfilesAsync(slot?.Id);
            Profiles = new ObservableCollection<Profile>(profiles);
            ProfileStatusText = profiles.Count > 0
                ? $"共 {profiles.Count} 个 Profile"
                : "eUICC 内没有 Profile";
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(EnabledProfile));
        }
        catch (Exception ex)
        {
            ProfileStatusText = $"读取失败：{ex.Message}";
        }
        finally
        {
            IsLoadingProfiles = false;
        }
    }

    private async Task LoadProfilesQuietlyAsync()
    {
        try
        {
            await Task.Delay(1500); // let the initial device scan settle first
            if (SelectedSlot == null) return;
            await App.Current?.Dispatcher.InvokeAsync(() => { })!;

            await LoadProfilesAsync();
        }
        catch
        {
            // Startup enrichment is best-effort; the panel keeps its placeholder.
        }
    }

    [RelayCommand]
    private async Task EnableProfileAsync(Profile? profile)
    {
        if (profile == null || SelectedSlot == null || IsBusy) return;
        IsBusy = true;
        StatusMessage = $"正在切换到 {profile.ProviderName}…";
        try
        {
            await _kernelService.SwitchEuiccProfileAsync(profile.ICCID, SelectedSlot.Id);
            await LoadProfilesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"切换 Profile 失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DisableProfileAsync(Profile? profile)
    {
        if (profile == null || SelectedSlot == null || IsBusy) return;
        IsBusy = true;
        StatusMessage = $"正在禁用 {profile.ProviderName}…";
        try
        {
            await _kernelService.DisableEuiccProfileAsync(profile.ICCID, SelectedSlot.Id);
            await LoadProfilesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"禁用 Profile 失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    // ================= VoWiFi actions =================

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "";

    [RelayCommand]
    private async Task ConnectVoWifiAsync()
    {
        if (!CanStartVoWifi || IsBusy) return;
        IsBusy = true;
        UserStopped = false;
        StatusMessage = "正在连接 VoWiFi…";
        try { await _kernelService.StartVoWifiAsync(SelectedSlot?.Id); }
        catch (Exception ex) { StatusMessage = $"连接出错：{ex.Message}"; }
        finally { IsBusy = false; OnPropertyChanged(string.Empty); }
    }

    [RelayCommand]
    private async Task DisconnectVoWifiAsync()
    {
        if (!CanStopVoWifi || IsBusy) return;
        IsBusy = true;
        try { await _kernelService.StopVoWifiAsync(SelectedSlot?.Id); }
        catch (Exception ex) { StatusMessage = $"断开出错：{ex.Message}"; }
        finally
        {
            IsBusy = false;
            UserStopped = true;
            OnPropertyChanged(string.Empty);
        }
    }

    [RelayCommand]
    private async Task ReconnectVoWifiAsync()
    {
        if (!CanReconnectVoWifi || IsBusy) return;
        IsBusy = true;
        UserStopped = false;
        StatusMessage = "正在重新连接 VoWiFi…";
        try
        {
            await _kernelService.StopVoWifiAsync(SelectedSlot?.Id);
            await _kernelService.StartVoWifiAsync(SelectedSlot?.Id);
        }
        catch (Exception ex) { StatusMessage = $"重连出错：{ex.Message}"; }
        finally { IsBusy = false; OnPropertyChanged(string.Empty); }
    }

    // ================= Technical details (advanced disclosure) =================

    public bool HasTechnicalDetails => VoWifiDiag != null;

    public string TechnicalSummary
    {
        get
        {
            var d = VoWifiDiag;
            if (d == null) return "暂无诊断数据。";
            var lines = new List<string>
            {
                $"状态机: {d.State}",
                $"ePDG: {d.EpdgFqdn}" + (string.IsNullOrWhiteSpace(d.EpdgIp) ? "" : $" -> {d.EpdgIp}:{d.EpdgPort}"),
                $"安全套件: {d.Suite} · DH {d.DhGroup} · EAP {d.EapMethod}"
            };
            if (d.Tunnel != null)
                lines.Add($"IPsec: 内 {d.Tunnel.InboundSpi:X8} / 外 {d.Tunnel.OutboundSpi:X8} · {d.Tunnel.EncryptionAlgorithm}/{d.Tunnel.IntegrityAlgorithm} · IP {d.Tunnel.AssignedIPv4}");
            if (d.Ims != null)
                lines.Add($"IMS: {d.Ims.RegistrationState} · 刷新 {d.Ims.ExpiresSeconds}s");
            lines.Add($"SIP 探针: {d.SipProbesSuccess}/{d.SipProbesSent} 成功 · 最后 RTT {(d.LastProbeRttMs > 0 ? $"{d.LastProbeRttMs} ms" : "—")}");
            if (!string.IsNullOrWhiteSpace(d.FailureStage))
                lines.Add($"失败阶段: {d.FailureStage} ({d.FailureCategory})");
            if (!string.IsNullOrWhiteSpace(d.LastError))
                lines.Add($"最后错误: {d.LastError}");
            return string.Join("\n", lines);
        }
    }

    [RelayCommand]
    private async Task CopyDiagnosticReportAsync()
    {
        try
        {
            var report = await _kernelService.BuildImsDiagnosticReportAsync(SelectedSlot?.Id);
            System.Windows.Clipboard.SetText(report);
        }
        catch
        {
            // Clipboard may be locked by another process; the failure is non-fatal.
        }
    }
}

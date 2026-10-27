using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoSharp.Euicc.Models;
using VoWin.Models;
using VoSharp.Kernel.Pool;
using VoWin.Services;
using VoWin.Helpers;
using VoWin.Views.Pages;
using Wpf.Ui.Controls;

namespace VoWin.ViewModels.Windows
{
    /// <summary>
    /// Navigation information architecture after the UX overhaul: the home
    /// dashboard is the landing page, daily-use pages (SMS, phone, eSIM) come
    /// next, and developer-oriented pages are grouped under Advanced tools so
    /// mainstream users never need to see protocol-level screens.
    /// </summary>
    public partial class MainWindowViewModel : ObservableObject
    {
        private readonly IVoKernelService _kernelService;

        [ObservableProperty]
        private string _applicationTitle = "VoWin";

        [ObservableProperty]
        private ObservableCollection<object> _menuItems = new()
        {
            new NavigationViewItem()
            {
                Content = "首页",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Home24 },
                TargetPageType = typeof(DashboardPage)
            },
            new NavigationViewItem()
            {
                Content = "短信",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Chat24 },
                TargetPageType = typeof(MessagesPage)
            },
            new NavigationViewItem()
            {
                Content = "电话",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Call24 },
                TargetPageType = typeof(PhonePage)
            },
            new NavigationViewItem()
            {
                Content = "eSIM 管理",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Sim24 },
                TargetPageType = typeof(ModemManagerPage)
            },
            // Bug-fix: LeftFluent 折叠模式下 NavigationViewItem 的子项（MenuItemsSource）
            // 不会生成可点击的 UI，导致"系统诊断/代理分流/远程控制"三个页面完全不可达。
            // 改为分组标题 + 顶级菜单项，保证全部页面都可访问。
            new NavigationViewItemSeparator(),
            new NavigationViewItem()
            {
                Content = "系统诊断",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Pulse24 },
                TargetPageType = typeof(SystemStatusPage)
            },
            new NavigationViewItem()
            {
                Content = "代理分流",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Globe24 },
                TargetPageType = typeof(ProxyPage)
            },
            new NavigationViewItem()
            {
                Content = "远程控制",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Bot24 },
                TargetPageType = typeof(RemoteControlPage)
            }
        };

        [ObservableProperty]
        private ObservableCollection<object> _footerMenuItems = new()
        {
            new NavigationViewItem()
            {
                Content = "设置",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(SettingsPage)
            },
        };

        [ObservableProperty]
        private ObservableCollection<Wpf.Ui.Controls.MenuItem> _trayMenuItems = new()
        {
            new Wpf.Ui.Controls.MenuItem { Header = "主界面", Tag = "tray_home" }
        };

        /// <summary>所有卡槽。</summary>
        public ObservableCollection<ModemSlot> Slots => _kernelService.Slots;

        /// <summary>左侧边栏 eSIM Profile 列表（普通实体 SIM 为空）。</summary>
        public ObservableCollection<EuiccProfileItem> EuiccProfiles { get; } = new();

        /// <summary>是否存在 eSIM Profile，用于切换列表/空态显示。</summary>
        public bool HasEuiccProfiles => EuiccProfiles.Count > 0;

        /// <summary>左侧边栏 eSIM 小标题（如"3 个 eSIM Profile"）。</summary>
        [ObservableProperty]
        private string _euiccSummary = "未检测到 eSIM";

        /// <summary>当前选中的卡槽（决定左侧 eSIM 列表来源）。</summary>
        [ObservableProperty]
        private ModemSlot? _selectedSlot;

        private EuiccProbeResult? _appliedProbe;
        private System.Windows.Threading.DispatcherTimer? _refreshTimer;

        public MainWindowViewModel(IVoKernelService kernelService)
        {
            _kernelService = kernelService;
            SelectedSlot = _kernelService.ActiveSlot ?? Slots.FirstOrDefault();

            // eSIM 探测结果同步：初始扫描阶段探测完成后服务层只更新
            // ModemSlot.LastEuiccProbe，并不触发 EuiccProbeCompleted 事件（该事件
            // 仅由切卡后重读路径触发）。因此除了订阅事件外，还需要在卡槽状态、
            // 信号/网络变化时从 LastEuiccProbe 缓存刷新，并叠加一段有界定时器
            // 兜底，确保左侧边栏 eSIM 列表在启动扫描后可靠填充。
            _kernelService.EuiccProbeCompleted += OnEuiccProbeCompleted;
            _kernelService.EgressRoutesChanged += OnEgressRoutesChanged;
            _kernelService.Kernel.SimStateChanged += (_, _) => OnKernelStateChanged();
            _kernelService.Kernel.TelephonyStateChanged += (_, _) => OnKernelStateChanged();
            _kernelService.Kernel.NetworkRegistrationChanged += (_, _) => OnKernelStateChanged();
            _kernelService.Kernel.VoWifiStateChanged += (_, _) => OnKernelStateChanged();
            _kernelService.Kernel.SignalQualityChanged += (_, _) => OnKernelStateChanged();
            Slots.CollectionChanged += (_, _) =>
            {
                if (SelectedSlot is null && Slots.Count > 0)
                {
                    SelectedSlot = Slots.FirstOrDefault();
                }
            };

            RefreshEuiccFromCache();

            // 有界兜底：启动后约 15 秒内周期性从缓存刷新，覆盖扫描完成时机
            // 与状态事件竞态，拿到可信数据后自停。
            _refreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _refreshTimer.Tick += (_, _) =>
            {
                RefreshEuiccFromCache();
                if (EuiccSummary != "未检测到 eSIM")
                {
                    _refreshTimer.Stop();
                }
            };
            _refreshTimer.Start();
        }

        private void OnEgressRoutesChanged()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(RefreshAllProfileAnnotations));
            }
            else
            {
                RefreshAllProfileAnnotations();
            }
        }

        private void RefreshAllProfileAnnotations()
        {
            foreach (var item in EuiccProfiles)
            {
                var (annot, phone, cardName) = BuildProfileAnnotation(item.Inner);
                item.UpdateAnnotation(annot, phone, cardName);
                UpdateItemStatus(item);
            }
        }

        private void UpdateItemStatus(EuiccProfileItem item)
        {
            var slot = _kernelService.Slots.FirstOrDefault(s =>
                string.Equals(s.Sim?.Iccid?.Trim(), item.ICCID, StringComparison.OrdinalIgnoreCase));
            if (slot is null) return;

            var diag = slot.VoWifiDiag;
            var state = diag?.State;
            var busy = state is VoSharp.Telephony.VoWifi.VoWifiState.ResolvingEpdg
                or VoSharp.Telephony.VoWifi.VoWifiState.ConnectingIkev2
                or VoSharp.Telephony.VoWifi.VoWifiState.AuthenticatingEapAka
                or VoSharp.Telephony.VoWifi.VoWifiState.IpsecTunnelEstablished
                or VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistering;
            item.IsBusy = busy;

            string status;
            switch (state)
            {
                case VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistered:
                    status = "VoWiFi 已连接";
                    break;
                case VoSharp.Telephony.VoWifi.VoWifiState.ResolvingEpdg:
                    status = "解析 ePDG…";
                    break;
                case VoSharp.Telephony.VoWifi.VoWifiState.ConnectingIkev2:
                case VoSharp.Telephony.VoWifi.VoWifiState.AuthenticatingEapAka:
                    status = "正在鉴权/建隧道…";
                    break;
                case VoSharp.Telephony.VoWifi.VoWifiState.IpsecTunnelEstablished:
                case VoSharp.Telephony.VoWifi.VoWifiState.ImsRegistering:
                    status = "注册 IMS…";
                    break;
                case VoSharp.Telephony.VoWifi.VoWifiState.Failed:
                    var failReason = Helpers.VoWifiStatePresenter.DescribeFailure(
                        diag?.FailureStage, diag?.FailureCategory, diag?.LastError);
                    status = $"连接失败：{failReason}";
                    break;
                case VoSharp.Telephony.VoWifi.VoWifiState.Disconnected:
                default:
                    status = slot.State == VoSharp.Kernel.Pool.SlotState.Online
                        ? "等待 VoWiFi 连接"
                        : $"设备状态：{slot.State}";
                    break;
            }
            item.StatusText = status;
        }

        private void OnKernelStateChanged()
        {
            if (System.Windows.Application.Current?.Dispatcher is { } dp && !dp.CheckAccess())
            {
                dp.BeginInvoke(new Action(() =>
                {
                    RefreshActiveSlot();
                    RefreshEuiccFromCache();
                }));
            }
            else
            {
                RefreshActiveSlot();
                RefreshEuiccFromCache();
            }
        }

        private void RefreshActiveSlot()
        {
            var active = _kernelService.ActiveSlot;
            if (!ReferenceEquals(active, SelectedSlot))
            {
                SelectedSlot = active;
            }
        }

        /// <summary>从当前卡槽的 LastEuiccProbe 缓存刷新侧栏，避免重复应用同一结果。</summary>
        private void RefreshEuiccFromCache()
        {
            var probe = SelectedSlot?.LastEuiccProbe;
            if (probe is null) return;
            if (ReferenceEquals(probe, _appliedProbe)) return;
            _appliedProbe = probe;
            ApplyEuiccProbeResult(probe);
        }

        private void OnEuiccProbeCompleted(string slotId, EuiccProbeResult result)
        {
            void Apply()
            {
                if (!string.Equals(SelectedSlot?.Id, slotId, StringComparison.OrdinalIgnoreCase)) return;
                if (ReferenceEquals(result, _appliedProbe)) return;
                _appliedProbe = result;
                ApplyEuiccProbeResult(result);
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(Apply));
            }
            else
            {
                Apply();
            }
        }

        /// <summary>按 ICCID 从代理规则表中查找路由，返回（卡片信息备注, 原始手机号）。
        /// 即使无手机号也返回国家名（无 IMSI 时回退 ICCID 尾号），便于区分归属地。</summary>
        private (string? Annotation, string? Phone, string? CardName) BuildProfileAnnotation(Profile profile)
        {
            if (string.IsNullOrWhiteSpace(profile.ICCID)) return (null, null, null);
            var iccid = profile.ICCID.Trim();
            var route = MatchRouteByIccid(iccid);
            var rawPhone = string.IsNullOrWhiteSpace(route?.PhoneNumber) ? null : route!.PhoneNumber.Trim();

            // 备注只显示国家名（如"英国"），仅用于区分归属地，不再拼接卡名/IMSI 尾号。
            var annotation = route?.CountryShortName;
            if (string.IsNullOrWhiteSpace(annotation))
                annotation = $"ICCID \u2026{CardDisplayNameResolver.IccidTail(iccid)}";

            // 卡片标题优先用解析卡名（ICCID 路由 CardName，能反映 MVNO 实际品牌），
            // 脏 PLMN 占位名除外。
            var cardName = route?.DisplayName;
            if (!string.IsNullOrWhiteSpace(cardName) && CarrierDisplayHelper.IsBogusPlmnLabel(cardName!))
                cardName = null;

            return (annotation, rawPhone, cardName);
        }

        private IccidRouteModel? MatchRouteByIccid(string iccid)
        {
            var exact = _kernelService.IccidRoutes.FirstOrDefault(r =>
                string.Equals(r.Iccid, iccid, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // 精确匹配失败时，尝试 ICCID 尾号唯一匹配（数据库备注按尾号收录）。
            var tail = CardDisplayNameResolver.IccidTail(iccid);
            if (tail.Length >= 4)
            {
                var tailMatches = _kernelService.IccidRoutes
                    .Where(r => !string.IsNullOrWhiteSpace(r.Iccid)
                        && r.Iccid.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (tailMatches.Count == 1) return tailMatches[0];
            }
            return null;
        }

        [RelayCommand]
        private void CopyPhone(string? phone)
        {
            if (!string.IsNullOrWhiteSpace(phone))
                System.Windows.Clipboard.SetText(phone.Trim());
        }

        /// <summary>手动编辑某张卡的手机号并落库（用于运营商不下发号码或物联网卡）。</summary>
        [RelayCommand]
        private void EditPhone(Profile? profile)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.ICCID)) return;

            var route = _kernelService.IccidRoutes.FirstOrDefault(r =>
                string.Equals(r.Iccid, profile.ICCID, StringComparison.OrdinalIgnoreCase));
            var current = string.IsNullOrWhiteSpace(route?.PhoneNumber) ? null : route!.PhoneNumber.Trim();

            var dialog = new Views.Windows.PhoneInputDialog(current)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.ResultPhone)) return;

            var newPhone = dialog.ResultPhone!.Trim();
            // 写库（IccidRoute.PhoneNumber），再刷新右侧 card。
            _kernelService.SaveIccidRoute(
                profile.ICCID,
                route?.ProxyNodeId,
                route?.CardName,
                route?.ImsiSnapshot,
                newPhone);
            RefreshAllProfileAnnotations();
        }

        private void ApplyEuiccProbeResult(EuiccProbeResult result)
        {
            var hasFreshData = result.Capability == EuiccCapability.Supported || result.Profiles.Count > 0;
            if (hasFreshData)
            {
                EuiccProfiles.Clear();
                foreach (var profile in result.Profiles)
                {
                    var (annot, phone, cardName) = BuildProfileAnnotation(profile);
                    EuiccProfiles.Add(new EuiccProfileItem(profile, annot, phone, cardName));
                }
                EuiccSummary = result.Profiles.Count > 0
                    ? $"{result.Profiles.Count} 个 eSIM Profile"
                    : "当前卡片无可用的 eSIM Profile";
                OnPropertyChanged(nameof(HasEuiccProfiles));
                return;
            }

            // 空结果不是终态：卡片可能尚未就绪。保留占位文案，但不清空已渲染的
            // 列表（避免后到的空探测把真实 Profile 抹掉），并安排一次重试探测。
            if (EuiccProfiles.Count == 0)
            {
                EuiccSummary = result.Capability switch
                {
                    EuiccCapability.Unsupported => "当前为普通实体 SIM",
                    _ => "未检测到 eSIM"
                };
                OnPropertyChanged(nameof(HasEuiccProfiles));
            }
            ScheduleEuiccRetry();
        }

        private int _euiccRetryCount;
        private System.Windows.Threading.DispatcherTimer? _euiccRetryTimer;

        /// <summary>
        /// 侧栏 eSIM 为空时按节奏重试 eUICC 探测。启动早期读卡器/卡可能还没就绪，
        /// 单次探测失败不应让列表永久空白。
        /// </summary>
        private void ScheduleEuiccRetry()
        {
            if (_euiccRetryTimer is not null) return;
            if (_euiccRetryCount >= 12) return; // 最多约 1 分钟
            _euiccRetryTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _euiccRetryTimer.Tick += async (_, _) =>
            {
                _euiccRetryCount++;
                if (EuiccProfiles.Count > 0 || _euiccRetryCount > 12)
                {
                    _euiccRetryTimer?.Stop();
                    return;
                }
                var slotId = SelectedSlot?.Id;
                if (string.IsNullOrWhiteSpace(slotId)) return;
                try
                {
                    var probe = await _kernelService.ProbeEuiccAsync(slotId);
                    if (probe.Capability == EuiccCapability.Supported || probe.Profiles.Count > 0)
                    {
                        ApplyEuiccProbeResult(probe);
                        _euiccRetryTimer?.Stop();
                    }
                }
                catch
                {
                    // 探测失败保持重试节奏
                }
            };
            _euiccRetryTimer.Start();
        }

        partial void OnSelectedSlotChanged(ModemSlot? value)
        {
            _appliedProbe = null;
            RefreshEuiccFromCache();
        }

        [RelayCommand]
        private async Task SwitchEuiccProfileAsync(Profile? profile)
        {
            if (profile is null || SelectedSlot is null) return;
            try
            {
                await _kernelService.SwitchEuiccProfileAsync(profile.ICCID, SelectedSlot.Id, profile.EuiccAid);
            }
            catch
            {
                // 侧栏切换失败无需弹窗；服务层已记录日志，Profile 列表稍后自动刷新。
            }
        }

        [RelayCommand]
        private async Task DisableEuiccProfileAsync(Profile? profile)
        {
            if (profile is null || SelectedSlot is null) return;
            try
            {
                await _kernelService.DisableEuiccProfileAsync(profile.ICCID, SelectedSlot.Id, profile.EuiccAid);
            }
            catch
            {
            }
        }

        [RelayCommand]
        private async Task RetryVoWifiAsync(Profile? profile)
        {
            _ = profile; // 触发按钮复用 Profile 参数；实际按当前选中槽重试。
            if (SelectedSlot is null) return;
            try
            {
                await _kernelService.StartVoWifiAsync(SelectedSlot.Id);
            }
            catch
            {
                // 重试失败无需弹窗；服务层已记录日志，状态文本随看门狗刷新。
            }
        }

        /// <summary>模板命令别名：eSIM Profile 卡片模板绑定此处。</summary>
        public IAsyncRelayCommand<Profile?> SwitchProfileCommand => SwitchEuiccProfileCommand;
        public IAsyncRelayCommand<Profile?> DisableProfileCommand => DisableEuiccProfileCommand;
    }
}

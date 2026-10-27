using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VoWin.Services;
using VoWin.Views.Pages;
using VoWin.ViewModels.Windows;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace VoWin.Views.Windows
{
    public partial class MainWindow : INavigationWindow
    {
        public MainWindowViewModel ViewModel { get; }
        public IVoKernelService KernelService { get; }

        private System.Windows.Interop.HwndSource? _hwndSource;
        private INavigationViewPageProvider? _pageProvider;

        public MainWindow(
            MainWindowViewModel viewModel,
            IVoKernelService kernelService,
            INavigationViewPageProvider navigationViewPageProvider,
            INavigationService navigationService,
            ISnackbarService snackbarService
        )
        {
            ViewModel = viewModel;
            KernelService = kernelService;
            DataContext = this;

            InitializeComponent();
            _pageProvider = navigationViewPageProvider;
            snackbarService.SetSnackbarPresenter(SnackbarPresenter);

            HookInCallFloatingWindow();

            // 应用启动或托盘导航时由 INavigationWindow.Navigate 驱动 MainFrame；
            // 这里兜底首次渲染首页，避免出现空白内容区。
            Loaded += (_, _) =>
            {
                if (MainFrame.Content == null)
                    Navigate(typeof(DashboardPage));
            };
        }

        #region INavigationWindow methods

        public INavigationView GetNavigation() => null!;

        public bool Navigate(Type pageType)
        {
            if (pageType == null) return false;
            UpdateNavSelection(pageType);
            if (MainFrame.Content?.GetType() == pageType) return true;
            // 页面通过依赖注入注册（含 ViewModel），必须从服务容器解析才能正确渲染。
            // 不能使用 Frame.Navigate(Type)，它只能用无参构造函数创建页面，导致空白内容区。
            if (VoWin.App.Services.GetService(pageType) is System.Windows.FrameworkElement page)
            {
                MainFrame.Content = page;
                return true;
            }
            return false;
        }

        public void SetPageService(INavigationViewPageProvider navigationViewPageProvider)
            => _pageProvider = navigationViewPageProvider;

        /// <summary>左侧导航按钮点击：Tag 映射页面类型并导航。</summary>
        private void OnNavClick(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b && b.Tag is string tag
                && _navPages.TryGetValue(tag, out var type))
            {
                Navigate(type);
            }
        }

        /// <summary>同步左侧导航选中态：当前页对应按钮高亮，其余还原。</summary>
        private void UpdateNavSelection(Type pageType)
        {
            foreach (var child in FindNavButtons(this))
            {
                var tag = (child.Tag as string)?.Split(':')[0];
                var active = _navPages.TryGetValue(tag ?? string.Empty, out var t) && t == pageType;
                NavProperties.SetIsActive(child, active);
            }
        }

        private System.Collections.Generic.IEnumerable<System.Windows.Controls.Button> FindNavButtons(DependencyObject root)
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.Controls.Button b && b.Tag is string)
                    yield return b;
                foreach (var nested in FindNavButtons(child))
                    yield return nested;
            }
        }

        private static readonly IReadOnlyDictionary<string, Type> _navPages =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
            {
                ["Dashboard"] = typeof(DashboardPage),
                ["Messages"] = typeof(MessagesPage),
                ["Phone"] = typeof(PhonePage),
                ["ModemManager"] = typeof(ModemManagerPage),
                ["SystemStatus"] = typeof(SystemStatusPage),
                ["Proxy"] = typeof(ProxyPage),
                ["RemoteControl"] = typeof(RemoteControlPage),
                ["Settings"] = typeof(SettingsPage),
            };

        public void ShowWindow() => Show();

        public void CloseWindow() => Close();

        #endregion INavigationWindow methods

        private void HookInCallFloatingWindow()
        {
            IsVisibleChanged += (s, e) => CheckInCallFloatingWindow();
            StateChanged += (s, e) => CheckInCallFloatingWindow();

            KernelService.Kernel.CallStateChanged += (s, e) =>
            {
                Dispatcher.BeginInvoke(CheckInCallFloatingWindow);
            };

            KernelService.Kernel.CallEnded += (s, e) =>
            {
                Dispatcher.BeginInvoke(CheckInCallFloatingWindow);
            };
        }

        private void CheckInCallFloatingWindow()
        {
            bool isCallActive = KernelService.CurrentCallState is VoSharp.Telephony.Calls.CallState.Active
                             or VoSharp.Telephony.Calls.CallState.Dialing
                             or VoSharp.Telephony.Calls.CallState.Ringing;

            bool isMainHidden = !IsVisible || WindowState == WindowState.Minimized;

            if (isCallActive && isMainHidden)
            {
                var num = !string.IsNullOrWhiteSpace(KernelService.CurrentCallNumber)
                    ? KernelService.CurrentCallNumber
                    : "当前通话";
                var presentedSlotId = KernelService.Kernel.ActiveCallSlotId;
                var slot = KernelService.Slots.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, presentedSlotId, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? KernelService.ActiveSlot?.Name ?? "SIM 1";

                InCallFloatingWindow.ShowActiveCall(
                    number: num,
                    durationText: "通话中",
                    slotInfo: slot,
                    onHangup: async () =>
                    {
                        await KernelService.HangupAsync();
                    },
                    onMuteChanged: isMuted =>
                    {
                    }
                );
            }
            else if (!isCallActive || !isMainHidden)
            {
                InCallFloatingWindow.Dismiss();
            }
        }

        /// <summary>
        /// Raises the closed event.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            // 移除窗口过程钩子与托盘回调钩子，避免 HwndSource / Shell_NotifyIcon
            // 在窗口关闭后继续被引用（链接与钩子加固）。
            _hwndSource?.RemoveHook(HwndMessageHook);
            TrayManager.Detach();

            base.OnClosed(e);

            // Make sure that closing this window will begin the process of closing the application.
            Application.Current.Shutdown();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Initialize system tray icon & minimize-to-tray handling
            TrayManager.Initialize(this);

            // Hook window procedure to listen for USB device arrival events
            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
            {
                _hwndSource = source;
                source.AddHook(HwndMessageHook);

            }
        }

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                int wp = wParam.ToInt32();
                if (wp == DBT_DEVICEARRIVAL)
                {
                    // USB device inserted!
                    var kernelService = App.Services.GetService(typeof(IVoKernelService)) as IVoKernelService;
                    kernelService?.OnUsbDeviceInserted();
                }
                else if (wp == DBT_DEVICEREMOVECOMPLETE)
                {
                    var kernelService = App.Services.GetService(typeof(IVoKernelService)) as IVoKernelService;
                    kernelService?.OnUsbDeviceRemoved();
                }
            }
            return IntPtr.Zero;
        }

        public void SetServiceProvider(IServiceProvider serviceProvider)
        {
            // Set service provider if supported
        }
    }
}

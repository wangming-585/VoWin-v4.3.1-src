using System.Globalization;
using System.Management;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using System.IO;

using System.Reflection;

using System.Diagnostics;

using System.Threading;

using System.Windows.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Documents;

using VoWin.Services;

using VoWin.ViewModels.Pages;

using VoWin.ViewModels.Windows;

using VoWin.Views.Pages;

using VoWin.Views.Windows;

using Wpf.Ui;

using Wpf.Ui.Appearance;

using Wpf.Ui.DependencyInjection;



namespace VoWin

{

    /// <summary>

    /// Interaction logic for App.xaml

    /// </summary>

    public partial class App

    {
    internal Services.DiagnosticMonitor? _diagnosticMonitor;


        private const string SingleInstanceMutexName = @"Local\VoWin.SingleInstance.v1";

        private const string SingleInstanceActivationEventName = @"Local\VoWin.ActivateExistingInstance.v1";

        // The.NET Generic Host provides dependency injection, configuration, logging, and other services.

        // https://docs.microsoft.com/dotnet/core/extensions/generic-host

        // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection

        // https://docs.microsoft.com/dotnet/core/extensions/configuration

        // https://docs.microsoft.com/dotnet/core/extensions/logging

        private static readonly IHost _host = Host

            .CreateDefaultBuilder()

            .ConfigureAppConfiguration(c => { c.SetBasePath(Path.GetDirectoryName(AppContext.BaseDirectory) ?? AppContext.BaseDirectory); })

            .ConfigureServices((context, services) =>

            {

                services.AddNavigationViewPageProvider();



                services.AddHostedService<ApplicationHostService>();



                // Theme manipulation

                services.AddSingleton<IThemeService, ThemeService>();



                // TaskBar manipulation

                services.AddSingleton<ITaskBarService, TaskBarService>();



                // Service containing navigation, same as INavigationWindow... but without window

                services.AddSingleton<INavigationService, NavigationService>();



                // In-app Snackbar / Toast notification service

                services.AddSingleton<ISnackbarService, SnackbarService>();



                // Main window with navigation

                services.AddSingleton<INavigationWindow, MainWindow>();

                services.AddSingleton<MainWindowViewModel>();



                // SQLite Preferences & Telecom Kernel Service

                services.AddSingleton<IPreferenceDatabaseService, PreferenceDatabaseService>();

                services.AddSingleton<IVoKernelService, VoKernelService>();

                services.AddSingleton<VoWin.Services.RemoteControl.RemoteControlService>();

                services.AddSingleton<IRemoteControlService>(sp => sp.GetRequiredService<VoWin.Services.RemoteControl.RemoteControlService>());

                services.AddHostedService(sp => sp.GetRequiredService<VoWin.Services.RemoteControl.RemoteControlService>());



                // Pages and ViewModels

                services.AddSingleton<DashboardPage>();

                services.AddSingleton<DashboardViewModel>();

                services.AddSingleton<ModemManagerPage>();

                services.AddSingleton<ModemManagerViewModel>();

                services.AddSingleton<PhonePage>();

                services.AddSingleton<PhoneViewModel>();

                services.AddSingleton<MessagesPage>();

                services.AddSingleton<MessagesViewModel>();

                services.AddSingleton<SystemStatusPage>();

                services.AddSingleton<SystemStatusViewModel>();

                services.AddSingleton<ProxyPage>();

                services.AddSingleton<ProxyViewModel>();

                services.AddSingleton<SettingsPage>();

                services.AddSingleton<SettingsViewModel>();

                services.AddSingleton<RemoteControlPage>();

                services.AddSingleton<RemoteControlViewModel>();

            }).Build();

        private bool _cellularAudioHelperMode;

        private Mutex? _singleInstanceMutex;

        private EventWaitHandle? _singleInstanceActivationEvent;

        private CancellationTokenSource? _singleInstanceListenerCts;

        private Task? _singleInstanceListenerTask;

        private bool _ownsSingleInstanceMutex;

        public static IServiceProvider Services

        {

            get { return _host.Services; }

        }



        private async void OnStartup(object sender, StartupEventArgs e)

        {
        // 诊断监控（必须最先初始化）
        try
        {
            var diagnosticDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "Diagnostics");
            _diagnosticMonitor = new Services.DiagnosticMonitor(diagnosticDir);
            _diagnosticMonitor.InstallGlobalHandlers();
            _diagnosticMonitor.LogCheckpoint("App.OnStartup 开始");

        // 全局默认字体：嵌入的微软雅黑，避免系统缺少字体时的渲染问题。
        try
        {
            if (TryFindResource("GlobalAppFontFamily") is FontFamily globalFont)
            {
                TextElement.FontFamilyProperty.OverrideMetadata(
                    typeof(TextElement), new FrameworkPropertyMetadata(globalFont));
                TextBlock.FontFamilyProperty.OverrideMetadata(
                    typeof(TextBlock), new FrameworkPropertyMetadata(globalFont));
                Control.FontFamilyProperty.OverrideMetadata(
                    typeof(Control), new FrameworkPropertyMetadata(globalFont));
                Window.FontFamilyProperty.OverrideMetadata(
                    typeof(Window), new FrameworkPropertyMetadata(globalFont));
            }
        }
        catch (Exception fontEx)
        {
            System.Diagnostics.Debug.WriteLine($"[VoWin] Global font override failed: {fontEx}");
        }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoWin] DiagnosticMonitor init failed: {ex}");
        }


            if (CellularAudioWorkerHost.IsRequested(e.Args))

            {

                _cellularAudioHelperMode = true;

                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                int exitCode;
                try { exitCode = await CellularAudioWorkerHost.RunAsync(e.Args); }
                catch { exitCode = -1; }

                Shutdown(exitCode);

                return;

            }



            if (!AcquireSingleInstance())

            {

                Shutdown(0);

                return;

            }



            // A pre-single-instance release cannot answer the activation event.

            // Detect it by process name as a final guard so two releases never

            // contend for the same modem ports.

            if (HasAnotherVoWinProcess())

            {

                ReleaseSingleInstance();

                MessageBox.Show(

                    "检测到另一个 VoWin 正在运行。请从托盘打开或先彻底退出已有实例，避免两个程序同时占用通信模组。",

                    "VoWin 已在运行",

                    MessageBoxButton.OK,

                    MessageBoxImage.Information);

                Shutdown(0);

                return;

            }



            AppDomain.CurrentDomain.UnhandledException += (s, exArgs) =>

            {

                LogCrash("AppDomain.UnhandledException", exArgs.ExceptionObject as Exception);

                // 保活：致命异常导致进程即将退出时，脱盘式拉起一个新实例接替，
                // 用户无感知；单实例互斥量随旧进程退出自动释放，不会冲突。
                ScheduleSelfRestart();

            };



            TaskScheduler.UnobservedTaskException += (s, taskArgs) =>

            {

                LogCrash("TaskScheduler.UnobservedTaskException", taskArgs.Exception);

                taskArgs.SetObserved();

            };



            try

            {

                RestoreThemePreference();

                ApplicationThemeManager.Changed += (_, _) =>

                    Dispatcher.BeginInvoke(ApplyBrandTheme, DispatcherPriority.Background);

                ApplyBrandTheme();

                StartSingleInstanceActivationListener();

                await _host.StartAsync();

                // _ = CheckForGitHubReleaseUpdateAsync();

            }

            catch (Exception ex)

            {

                LogCrash("OnStartup._host.Start", ex);

            }

        }



        /// <summary>
        /// 致命崩溃后用 WMI 脱盘式重启自身（不依赖当前进程存活）。
        /// 5 分钟内最多重拉 3 次，防止崩溃循环拖垮系统。
        /// </summary>
        private static void ScheduleSelfRestart()
        {
            try
            {
                var stampFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin", "restart-stamps.txt");
                var now = DateTime.UtcNow;
                var recent = new List<DateTime>();
                if (File.Exists(stampFile))
                {
                    recent.AddRange(File.ReadAllLines(stampFile)
                        .Select(line => DateTime.TryParse(line, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
                        .Where(t => now - t < TimeSpan.FromMinutes(5)));
                }
                if (!Helpers.KeepAlivePolicy.ShouldAllowSelfRestart(recent, now)) return; // 熙断：5 分钟内已重启 3 次，停止重拉
                recent.Add(now);
                Directory.CreateDirectory(Path.GetDirectoryName(stampFile)!);
                File.WriteAllLines(stampFile, recent.Select(t => t.ToString("o", CultureInfo.InvariantCulture)));

                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var searcher = new System.Management.ManagementObjectSearcher(
                            "SELECT * FROM Win32_Process WHERE Name = 'vowin.exe'");
                        // 等旧进程真正退出（最多 15 秒），避免单实例互斥量争拗
                        for (int i = 0; i < 15; i++)
                        {
                            if (!searcher.Get().Cast<System.Management.ManagementObject>().Any()) break;
                            Thread.Sleep(1000);
                        }
                    }
                    catch { }
                    try
                    {
                        var start = new System.Management.ManagementClass("Win32_ProcessStartup")
                            .CreateInstance();
                        start["ShowWindow"] = 1; // SW_SHOWNORMAL
                        var inParams = new System.Management.ManagementClass("Win32_Process")
                            .GetMethodParameters("Create");
                        inParams["CommandLine"] = $"\"{exe}\"";
                        inParams["ProcessStartupInformation"] = start;
                        using var processClass = new System.Management.ManagementClass("Win32_Process");
                        var outParams = processClass.InvokeMethod("Create", inParams, null);
                        LogCrash("SelfRestart", new Exception($"ReturnCode={outParams?["ReturnValue"]}"));
                    }
                    catch (Exception restartEx)
                    {
                        LogCrash("SelfRestart", restartEx);
                    }
                });
            }
            catch { }
        }

        private static void LogCrash(string source, Exception? ex)

        {

            try

            {

                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin");

                Directory.CreateDirectory(logDir);

                var crashFile = Path.Combine(logDir, "crash.log");

                File.AppendAllText(crashFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\r\n\r\n");

            }

            catch (Exception fileEx)
            {
                System.Diagnostics.Debug.WriteLine($"[VoWin] Crash-log write failed: {fileEx}");
            }

        }



        /// <summary>

        /// Applies VoWin's adaptive light-blue / navy-black visual system after

        /// WPF-UI swaps its theme resources.

        /// </summary>

        public static void ApplyBrandTheme()

        {

            var res = System.Windows.Application.Current?.Resources;

            if (res == null) return;



            var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

            var palette = dark

                ? new Dictionary<string, string>

                {

                    ["AppSurfaceBrush"] = "#1E1E20",

                    ["AppNavigationBrush"] = "#262628",

                    ["AppCardBrush"] = "#2C2C2E",

                    ["AppCardElevatedBrush"] = "#323234",

                    ["AppCardBorderBrush"] = "#3A3A3E",

                    ["AppDividerBrush"] = "#38383C",

                    ["AppInputBrush"] = "#252527",

                    ["AppHoverBrush"] = "#3A3A3E",

                    ["AppSelectedBrush"] = "#0F3060",

                    ["AppOverlayBrush"] = "#99000000",

                    ["AppTextPrimaryBrush"] = "#F5F5F7",

                    ["AppTextSecondaryBrush"] = "#98989E",

                    ["AppTextTertiaryBrush"] = "#7C7C82",

                    ["AppTextOnAccentBrush"] = "#FFFFFF",

                    ["AppTextOnAccentSecondaryBrush"] = "#E5F0FF",

                    ["AppAccentBrush"] = "#0A84FF",

                    ["AppAccentHoverBrush"] = "#3395FF",

                    ["AppAccentPressedBrush"] = "#0069DB",

                    ["AppAccentSoftBrush"] = "#0D2B4F",

                    ["AppAccentSoftBorderBrush"] = "#1E4E8C",

                    ["AppSuccessBrush"] = "#30D158",

                    ["AppSuccessSoftBrush"] = "#0C3A19",

                    ["AppWarningBrush"] = "#FF9F0A",

                    ["AppWarningSoftBrush"] = "#3D2606",

                    ["AppDangerBrush"] = "#FF453A",

                    ["AppDangerSoftBrush"] = "#421515",

                    ["AppInfoBrush"] = "#64D2FF",

                    ["AppInfoSoftBrush"] = "#0A3142",

                    ["ApplicationBackgroundBrush"] = "#1E1E20",

                    ["CardBackgroundFillColorDefaultBrush"] = "#2C2C2E",

                    ["CardBackgroundFillColorSecondaryBrush"] = "#252527",

                    ["CardStrokeColorDefaultBrush"] = "#3A3A3E"

                }

                : new Dictionary<string, string>

                {

                    ["AppSurfaceBrush"] = "#F5F5F7",

                    ["AppNavigationBrush"] = "#ECECEE",

                    ["AppCardBrush"] = "#FFFFFF",

                    ["AppCardElevatedBrush"] = "#FFFFFF",

                    ["AppCardBorderBrush"] = "#E3E3E6",

                    ["AppDividerBrush"] = "#E5E5EA",

                    ["AppInputBrush"] = "#F2F2F4",

                    ["AppHoverBrush"] = "#E8E8EC",

                    ["AppSelectedBrush"] = "#DCE7FA",

                    ["AppOverlayBrush"] = "#59000000",

                    ["AppTextPrimaryBrush"] = "#1D1D1F",

                    ["AppTextSecondaryBrush"] = "#6E6E73",

                    ["AppTextTertiaryBrush"] = "#8E8E93",

                    ["AppTextOnAccentBrush"] = "#FFFFFF",

                    ["AppTextOnAccentSecondaryBrush"] = "#DCE8FF",

                    ["AppAccentBrush"] = "#007AFF",

                    ["AppAccentHoverBrush"] = "#0071E3",

                    ["AppAccentPressedBrush"] = "#0064D2",

                    ["AppAccentSoftBrush"] = "#E5F0FF",

                    ["AppAccentSoftBorderBrush"] = "#B3D4FC",

                    ["AppSuccessBrush"] = "#248A3D",

                    ["AppSuccessSoftBrush"] = "#E8F8EC",

                    ["AppWarningBrush"] = "#C93400",

                    ["AppWarningSoftBrush"] = "#FFF3E5",

                    ["AppDangerBrush"] = "#D70015",

                    ["AppDangerSoftBrush"] = "#FFEBEE",

                    ["AppInfoBrush"] = "#007AFF",

                    ["AppInfoSoftBrush"] = "#E5F0FF",

                    ["ApplicationBackgroundBrush"] = "#F5F5F7",

                    ["CardBackgroundFillColorDefaultBrush"] = "#FFFFFF",

                    ["CardBackgroundFillColorSecondaryBrush"] = "#F5F5F7",

                    ["CardStrokeColorDefaultBrush"] = "#E3E3E6"

                };



            foreach (var (key, color) in palette)

            {

                var nextColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);

                if (res[key] is System.Windows.Media.SolidColorBrush existing && !existing.IsFrozen)

                {

                    // Mutating the existing brush also refreshes values returned

                    // by converters and view-model bindings, not only DynamicResource.

                    existing.Color = nextColor;

                }

                else

                {

                    res[key] = new System.Windows.Media.SolidColorBrush(nextColor);

                }

            }



            var accent = (System.Windows.Media.SolidColorBrush)res["AppAccentBrush"];

            var accentHover = (System.Windows.Media.SolidColorBrush)res["AppAccentHoverBrush"];

            var accentPressed = (System.Windows.Media.SolidColorBrush)res["AppAccentPressedBrush"];

            var disabled = new System.Windows.Media.SolidColorBrush(accent.Color) { Opacity = 0.32 };

            disabled.Freeze();



            res["ToggleSwitchFillOn"] = accent;

            res["ToggleSwitchFillOnPointerOver"] = accentHover;

            res["ToggleSwitchFillOnPressed"] = accentPressed;

            res["ToggleSwitchStrokeOn"] = accent;

            res["ToggleSwitchStrokeOnPointerOver"] = accentHover;

            res["ToggleSwitchStrokeOnPressed"] = accentPressed;

            res["ToggleSwitchFillOnDisabled"] = disabled;

            res["ToggleButtonBackgroundChecked"] = accent;

            res["ToggleButtonBackgroundCheckedPointerOver"] = accentHover;

            res["ToggleButtonBackgroundCheckedPressed"] = accentPressed;

            res["ToggleButtonBorderBrushChecked"] = accent;

            res["ToggleButtonBorderBrushCheckedPressed"] = accentPressed;

            // Some status elements receive a brush from a view-model or a value

            // converter rather than directly through DynamicResource. Re-evaluate

            // those bindings after the palette has been replaced.

            Helpers.ThemeBrushes.NotifyThemeResourcesRefreshed();

        }



        private async Task CheckForGitHubReleaseUpdateAsync()

        {

            try

            {

                // Do not delay the main window or modem initialization for a

                // network request. GitHub failures are intentionally silent.

                var installed = Assembly.GetExecutingAssembly().GetName().Version;

                if (installed == null) return;

                var update = await new GitHubReleaseUpdateService().CheckAsync(installed, CancellationToken.None);

                if (update == null) return;



                await Dispatcher.InvokeAsync(() =>

                {

                    var result = MessageBox.Show(

                        $"发现新版本 VoWin v{update.Version.ToString(3)}。\n当前版本：v{installed.ToString(3)}\n\n是否打开 GitHub 下载页面？",

                        "发现新版本",

                        MessageBoxButton.YesNo,

                        MessageBoxImage.Information);

                    if (result == MessageBoxResult.Yes)

                    {

                        using var _ = Process.Start(new ProcessStartInfo

                        {

                            FileName = update.ReleasePageUrl,

                            UseShellExecute = true

                        });

                    }

                }, DispatcherPriority.ApplicationIdle);

            }

            catch

            {

                // Update checking is best-effort and must never affect startup.

            }

        }



        private bool AcquireSingleInstance()

        {

            try

            {

                _singleInstanceActivationEvent = new EventWaitHandle(

                    initialState: false,

                    mode: EventResetMode.AutoReset,

                    name: SingleInstanceActivationEventName);

                _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);

                _ownsSingleInstanceMutex = createdNew;

                if (createdNew) return true;



                try { _singleInstanceActivationEvent.Set(); } catch { }

                _singleInstanceActivationEvent.Dispose();

                _singleInstanceActivationEvent = null;

                _singleInstanceMutex.Dispose();

                _singleInstanceMutex = null;

                return false;

            }

            catch (Exception ex)

            {

                LogCrash("SingleInstance.Acquire", ex);

                // Failing open here could let two processes seize hardware, so

                // prefer a safe exit if Windows cannot create the named objects.

                MessageBox.Show("VoWin 无法确认是否已有运行实例，已取消启动以保护通信模组。", "VoWin 启动受保护", MessageBoxButton.OK, MessageBoxImage.Warning);

                return false;

            }

        }



        private static bool HasAnotherVoWinProcess()

        {

            try

            {

                using var current = Process.GetCurrentProcess();

                return Process.GetProcessesByName(current.ProcessName)

                    .Any(process =>

                    {

                        try { return process.Id != current.Id; }

                        finally { process.Dispose(); }

                    });

            }

            catch

            {

                return false;

            }

        }



        private void StartSingleInstanceActivationListener()

        {

            var activationEvent = _singleInstanceActivationEvent;

            if (activationEvent == null) return;



            var cts = new CancellationTokenSource();

            _singleInstanceListenerCts = cts;

            _singleInstanceListenerTask = Task.Run(() =>

            {

                try

                {

                    while (!cts.IsCancellationRequested)

                    {

                        if (!activationEvent.WaitOne(500) || cts.IsCancellationRequested) continue;

                        Dispatcher.BeginInvoke(() =>

                        {

                            if (!cts.IsCancellationRequested)

                                TrayManager.RestoreMainWindow();

                        }, DispatcherPriority.ApplicationIdle);

                    }

                }

                catch (ObjectDisposedException) { }

            });

        }



        private void ReleaseSingleInstance()

        {

            var cts = Interlocked.Exchange(ref _singleInstanceListenerCts, null);

            cts?.Cancel();

            try { _singleInstanceActivationEvent?.Set(); } catch { }

            try { _singleInstanceActivationEvent?.Dispose(); } catch { }

            _singleInstanceActivationEvent = null;

            if (_ownsSingleInstanceMutex)

            {

                try { _singleInstanceMutex?.ReleaseMutex(); } catch { }

            }

            _ownsSingleInstanceMutex = false;

            try { _singleInstanceMutex?.Dispose(); } catch { }

            _singleInstanceMutex = null;

            cts?.Dispose();

        }



        [Obsolete("Use ApplyBrandTheme instead.")]

        public static void ApplyGreenToggleTheme() => ApplyBrandTheme();



        private static string ThemePreferencePath => Path.Combine(

            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),

            "VoWin",

            "ui-theme.txt");



        private static void RestoreThemePreference()

        {

            try

            {

                if (!File.Exists(ThemePreferencePath)) return;

                var saved = File.ReadAllText(ThemePreferencePath).Trim();

                if (Enum.TryParse<ApplicationTheme>(saved, true, out var theme) &&

                    theme is ApplicationTheme.Light or ApplicationTheme.Dark)

                {

                    ApplicationThemeManager.Apply(theme);

                }

            }

            catch

            {

                // Theme persistence must never prevent the app from starting.

            }

        }



        public static void SaveThemePreference(ApplicationTheme theme)

        {

            if (theme is not (ApplicationTheme.Light or ApplicationTheme.Dark)) return;

            try

            {

                var directory = Path.GetDirectoryName(ThemePreferencePath)!;

                Directory.CreateDirectory(directory);

                File.WriteAllText(ThemePreferencePath, theme.ToString());

            }

            catch

            {

                // A read-only profile should not break live theme switching.

            }

        }



        /// <summary>

        /// Occurs when the application is closing.

        /// </summary>

        private async void OnExit(object sender, ExitEventArgs e)

        {
        _diagnosticMonitor?.LogCheckpoint("App.OnExit 调用，进程即将退出");
        _diagnosticMonitor?.Dispose();


            ReleaseSingleInstance();

            if (_cellularAudioHelperMode)

            {

                try { _host.Dispose(); } catch { }

                return;

            }



            var kernelService = _host.Services.GetService<IVoKernelService>();

            if (kernelService != null)

            {

                try { await kernelService.DisposeAsync(); } catch { }

            }



            try

            {

                await _host.StopAsync();

            }

            catch { }



            try

            {

                _host.Dispose();

            }

            catch { }

        }



        /// <summary>

        /// Occurs when an exception is thrown by an application but not handled.

        /// </summary>

        // UI 线程异常熎断：短时间内反复崩溃时不再吞异常，转致命重启，
        // 避免“卡死但杀不死”的僵尸 UI 状态。
        private readonly Queue<DateTime> _uiCrashStamps = new();

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)

        {

            try

            {

                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoWin");

                Directory.CreateDirectory(logDir);

                var crashFile = Path.Combine(logDir, "crash.log");

                File.AppendAllText(crashFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] DispatcherUnhandledException: {e.Exception}\r\n\r\n");

            }

            catch { }



            var now = DateTime.UtcNow;
            _uiCrashStamps.Enqueue(now);
            if (Helpers.KeepAlivePolicy.ShouldTriggerUiCircuitBreaker(_uiCrashStamps, now))
            {
                LogCrash("DispatcherUnhandledException.CircuitBreaker",
                    new Exception($"{_uiCrashStamps.Count} UI exceptions within 1 minute; requesting self-restart."));
                ScheduleSelfRestart();
                Environment.Exit(1);
                return;
            }

            e.Handled = true;

        }

    }

}

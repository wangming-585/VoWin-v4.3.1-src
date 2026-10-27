using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VoSharp.Common.Utils;
using System.Windows.Threading;

namespace VoWin.Services;

/// <summary>
/// 诊断监控器：捕获所有退出路径、未处理异常、生成崩溃转储
/// </summary>
public sealed class DiagnosticMonitor : IDisposable
{
    private readonly string _logPath;
    private readonly string _dumpPath;
    private readonly object _logLock = new();
    private bool _isFirstChanceExceptionLogged;
    private int _disposed;

    // P/Invoke for Minidump generation
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        int dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    private const int MiniDumpWithFullMemory = 0x00000002;
    private const int MiniDumpWithHandleData = 0x00000004;
    private const int MiniDumpWithThreadInfo = 0x00001000;
    private const int MiniDumpWithUnloadedModules = 0x00002000;

    public DiagnosticMonitor(string logDirectory)
    {
        var now = DateTime.Now;
        _logPath = Path.Combine(logDirectory, $"diagnostic_{now:yyyyMMdd_HHmmss}.log");
        _dumpPath = Path.Combine(logDirectory, $"crash_{now:yyyyMMdd_HHmmss}.dmp");

        Directory.CreateDirectory(logDirectory);
        Log($"[DiagnosticMonitor] 启动于 {now:yyyy-MM-dd HH:mm:ss.fff}");
        Log($"[DiagnosticMonitor] 进程 PID={Process.GetCurrentProcess().Id}, 工作目录={Environment.CurrentDirectory}");
        Log($"[DiagnosticMonitor] .NET={Environment.Version}, OS={Environment.OSVersion}");
    }

    public void InstallGlobalHandlers()
    {
        // 1. AppDomain 未处理异常（包括非UI线程）
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        Log("[DiagnosticMonitor] 已注册 AppDomain.UnhandledException");

        // 2. Task 未观察到的异常
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Log("[DiagnosticMonitor] 已注册 TaskScheduler.UnobservedTaskException");

        // 3. WPF Dispatcher 未处理异常（UI线程）
        if (Application.Current != null)
        {
            Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
            Log("[DiagnosticMonitor] 已注册 Application.DispatcherUnhandledException");
        }

        // 4. 进程退出事件（正常+异常）
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Log("[DiagnosticMonitor] 已注册 AppDomain.ProcessExit");

        // 5. 首次机会异常（调试用，可选）
        // AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;

        // 6. fire-and-forget 弃元任务的统一异常观察点（SafeFire）
        SafeFire.OnFault = (caller, ex) => Log($"[SafeFire] fire-and-forget fault at {caller}: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");

        Log("[DiagnosticMonitor] 全局异常监控已就绪");
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        var msg = new StringBuilder();
        msg.AppendLine("=== AppDomain.UnhandledException ===");
        msg.AppendLine($"IsTerminating: {e.IsTerminating}");
        msg.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        msg.AppendLine($"Exception: {ex?.GetType().FullName ?? e.ExceptionObject?.GetType().FullName ?? "null"}");
        msg.AppendLine($"Message: {ex?.Message ?? e.ExceptionObject?.ToString() ?? "null"}");
        msg.AppendLine($"StackTrace:\n{ex?.StackTrace ?? "(none)"}");
        AppendThreadDump(msg);
        Log(msg.ToString());

        if (e.IsTerminating)
        {
            TryGenerateMinidump($"unhandled-exception-{ex?.GetType().Name ?? "unknown"}");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var msg = new StringBuilder();
        msg.AppendLine("=== TaskScheduler.UnobservedTaskException ===");
        msg.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        msg.AppendLine($"Observed: {e.Observed}");
        msg.AppendLine($"Exception: {e.Exception}");
        AppendThreadDump(msg);
        Log(msg.ToString());

        // 标记为已观察，防止进程崩溃
        e.SetObserved();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var msg = new StringBuilder();
        msg.AppendLine("=== Application.DispatcherUnhandledException ===");
        msg.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        msg.AppendLine($"Exception: {e.Exception.GetType().FullName}");
        msg.AppendLine($"Message: {e.Exception.Message}");
        msg.AppendLine($"StackTrace:\n{e.Exception.StackTrace}");
        AppendThreadDump(msg);
        Log(msg.ToString());

        // 标记为已处理，防止应用崩溃（仅在非严重异常时）
        if (!IsFatalException(e.Exception))
        {
            e.Handled = true;
            Log("[DiagnosticMonitor] 异常已被标记为已处理，应用继续运行");
        }
        else
        {
            Log("[DiagnosticMonitor] 严重异常，允许应用崩溃");
            TryGenerateMinidump($"dispatcher-fatal-{e.Exception.GetType().Name}");
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        var msg = new StringBuilder();
        msg.AppendLine("=== AppDomain.ProcessExit ===");
        msg.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        msg.AppendLine($"ExitCode: {Environment.ExitCode}");
        msg.AppendLine($"HasShutdownStarted: {Environment.HasShutdownStarted}");
        AppendThreadDump(msg);
        Log(msg.ToString());
        Log("[DiagnosticMonitor] 进程即将退出");
    }

    private void OnFirstChanceException(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        // 首次机会异常会非常频繁，仅记录一次提示
        if (!_isFirstChanceExceptionLogged)
        {
            Log($"[DiagnosticMonitor] FirstChanceException 监控已启用（首次：{e.Exception.GetType().Name}）");
            _isFirstChanceExceptionLogged = true;
        }
    }

    private bool IsFatalException(Exception ex)
    {
        return ex is OutOfMemoryException
            || ex is StackOverflowException
            || ex is AccessViolationException
            || ex is NullReferenceException
            || ex is InvalidOperationException && ex.Message.Contains("Dispatcher");
    }

    private void AppendThreadDump(StringBuilder sb)
    {
        sb.AppendLine("--- Active Threads ---");
        var process = Process.GetCurrentProcess();
        foreach (ProcessThread thread in process.Threads)
        {
            try
            {
                sb.AppendLine($"  TID={thread.Id}, State={thread.ThreadState}, Priority={thread.PriorityLevel}, CPU={thread.TotalProcessorTime}");
            }
            catch
            {
                // 线程可能已退出
            }
        }
        sb.AppendLine($"--- Managed Threads: {ThreadPool.ThreadCount} ---");
    }

    private void TryGenerateMinidump(string reason)
    {
        try
        {
            Log($"[DiagnosticMonitor] 尝试生成 Minidump: {reason}");
            var process = Process.GetCurrentProcess();
            using var fs = new FileStream(_dumpPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var dumpType = MiniDumpWithFullMemory | MiniDumpWithHandleData | MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules;
            bool success = MiniDumpWriteDump(
                process.Handle,
                (uint)process.Id,
                fs.SafeFileHandle.DangerousGetHandle(),
                dumpType,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (success)
            {
                Log($"[DiagnosticMonitor] Minidump 已生成: {_dumpPath} ({new FileInfo(_dumpPath).Length} bytes)");
            }
            else
            {
                Log($"[DiagnosticMonitor] Minidump 生成失败: Win32Error={Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            Log($"[DiagnosticMonitor] Minidump 生成异常: {ex}");
        }
    }

    private void Log(string message)
    {
        lock (_logLock)
        {
            try
            {
                File.AppendAllText(_logPath, message + Environment.NewLine);
            }
            catch
            {
                // 日志写入失败时静默忽略
            }
        }
    }

    public void LogCheckpoint(string checkpoint)
    {
        Log($"[CHECKPOINT] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {checkpoint}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Log($"[DiagnosticMonitor] Dispose 调用于 {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            if (Application.Current != null)
            {
                Application.Current.DispatcherUnhandledException -= OnDispatcherUnhandledException;
            }
        }
    }
}

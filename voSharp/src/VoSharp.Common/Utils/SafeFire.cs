namespace VoSharp.Common.Utils;

/// <summary>
/// Fire-and-forget 任务的统一安全封装：异常被捕获并送入诊断钩子，
/// 避免"赋值给弃元丢弃 Task"导致的静默异常丢失。
/// </summary>
public static class SafeFire
{
    /// <summary>诊断钩子（可选）。VoWin 启动时接入 DiagnosticMonitor。</summary>
    public static Action<string, Exception>? OnFault;

    public static void Run(Func<Task>? asyncAction, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (asyncAction is null) return;
        Run(asyncAction(), caller);
    }

    public static void Run(Task task, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (task is null) return;
        _ = ObserveAsync(task, caller);
    }

    private static async Task ObserveAsync(Task task, string caller)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { OnFault?.Invoke(caller, ex); } catch { }
        }
    }
}

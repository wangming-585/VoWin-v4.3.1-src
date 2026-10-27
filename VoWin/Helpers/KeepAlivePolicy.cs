using System;
using System.Collections.Generic;
using System.Linq;

namespace VoWin.Helpers;

/// <summary>
/// 保活策略的纯判定逻辑（无 IO、无 WMI），便于单元测试与 App.xaml.cs 复用。
/// </summary>
internal static class KeepAlivePolicy
{
    /// <summary>
    /// 崩溃自重启熔断：5 分钟窗口内最多 3 次重启，超出则放弃（防止崩溃风暴无限拉起）。
    /// </summary>
    public static bool ShouldAllowSelfRestart(IReadOnlyList<DateTime> recentStamps, DateTime nowUtc,
        int maxRestarts = 3, double windowMinutes = 5)
    {
        return recentStamps.Count(t => t != DateTime.MinValue && nowUtc - t < TimeSpan.FromMinutes(windowMinutes)) < maxRestarts;
    }

    /// <summary>
    /// UI 异常熔断：1 分钟窗口内异常次数达到阈值则触发自重启退出。
    /// </summary>
    public static bool ShouldTriggerUiCircuitBreaker(Queue<DateTime> stamps, DateTime nowUtc,
        int threshold = 20, double windowSeconds = 60)
    {
        while (stamps.Count > 0 && nowUtc - stamps.Peek() > TimeSpan.FromSeconds(windowSeconds))
            stamps.Dequeue();
        return stamps.Count >= threshold;
    }

    /// <summary>
    /// 解析 restart-stamps.txt 的历史时间戳（容错坏行）。
    /// </summary>
    public static List<DateTime> ParseRestartStamps(IEnumerable<string> lines, DateTime nowUtc,
        double windowMinutes = 5)
    {
        var window = TimeSpan.FromMinutes(windowMinutes);
        return lines
            .Select(l => DateTime.TryParse(l, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
            .Where(t => t != DateTime.MinValue && nowUtc - t < window)
            .ToList();
    }
}

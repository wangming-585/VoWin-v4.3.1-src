# VoWin 深度优化基线（2026-10-25 采集）

测量环境：Windows Server（10.0.28000），.NET 10.0.13，Debug 构建。
运行实例：PID 109828（旧构建，含保活修复前代码路径中已有的启动埋点）。

## B0.1 启动耗时（DiagnosticMonitor 埋点，近 10 次启动）

| 阶段 | 中位数 | 范围 |
|------|--------|------|
| App.OnStartup → VoKernelService.InitializeAsync 开始 | ~1.02 s | 788–1282 ms |
| InitializeAsync → StartWatchdogs（整个初始化+扫描） | ~0.72 s | 640–1100 ms |
| 合计 OnStartup → 看门狗启动 | ~1.75 s | 1642–2329 ms |

观察：OnStartup → InitializeAsync 之间约 1 秒全部消耗在主窗口创建/XAML 解析/DI 容器构建上（尚未埋更细的检查点）；InitializeAsync 内部 SQLite 全量短信加载 + 串口扫描 + eUICC 探测是串行的。

## B0.2 内存（运行约 3.5 小时的实例）

- WorkingSet: 217.7 MB
- PrivateMemory: 116.4 MB
- 句柄: 1633，线程: 39

## B0.3 测试时长（493 项，--no-build）

- 测试器内耗时：688–1000 ms（中位 ~710 ms）
- 含 dotnet CLI 启动的墙钟：2.4–2.9 s

## B0.4 体积

- Assets/vowin.png: 1041 KB（PNG 资源，嵌入 exe）
- 主 exe（framework-dependent 单文件）：构建后测量

## 优化目标

| 指标 | 基线 | 目标 |
|------|------|------|
| OnStartup → 看门狗启动 | 1750 ms | ≤ 1200 ms |
| vowin.png | 1041 KB | ≤ 150 KB |
| WorkingSet | 217.7 MB | 下降 ≥ 15%（短信分页后按数据量再评估） |
| 测试回归 | 493 全绿 | 每项优化后保持全绿 |

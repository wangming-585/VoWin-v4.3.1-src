# VoWin 深度优化报告

日期：2026-10-25 ｜ 基线对照：docs/optimization-baseline.md

## 结果总览

| 维度 | 优化前（基线） | 优化后 | 说明 |
|---|---|---|---|
| 应用图标 | 1041 KB (1254×1254) | **50 KB (256×256)** | -95%，256px 覆盖窗口/托盘/About 全部用途 |
| 启动数据加载 | 6 个 SQLite 查询串行 | **全部并发启动** | 消除串行等待；OnStartup→InitializeAsync 埋点对比待新实例切换后采集 |
| 短信加载 | 全表加载 | **分页最近 500 条**（`GetRecentSmsMessagesAsync`，DESC LIMIT + 索引） | 短信积累后启动内存与耗时不再随历史增长 |
| 远程 OTP 查询 | 全表加载 | 最近 50 条 | 同上 |
| DB 损坏 | 启动即崩 | **隔离坏档 → 重建 → 共享内存降级**（`IsDegraded` 标记） | 保证可启动，坏档保留为 `.corrupt-时间戳` |
| VoKernelService.cs | 3608 行 | **2430 行 + 3 个 partial**（Euicc/ProxyRouting/Audio） | 按功能分区，行为零变化 |
| 遗留备份文件 | 5 个 .bak_esim* | 已删除（回收站可恢复，全项目无引用） | |
| 测试 | 493 项 | **505 项**（+4 短信分页快照、+6 保活熔断、+2 DB 容错） | 10 轮连续压测全部 505/505 通过 |

## 性能（P1）

- **P1.1 启动并行化**：`VoKernelService.InitializeAsync` 中通话记录/短信/代理节点/国家路由/ICCID 路由/legacy SIM 偏好 6 个查询改为先并发启动再按依赖 await；新增 3 个诊断埋点。eUICC 探测与槽位偏好恢复保持串行（避免 APDU 竞争），仅加埋点。
- **P1.2 资源瘦身**：`Assets/vowin.png` 1041KB→50KB（LANCZOS 优化压缩），原图已备份至 `backups/vowin-1254px-original.png`。
- **P1.3 短信分页**：新增 `GetRecentSmsMessagesAsync(int limit)`（`ORDER BY Timestamp DESC LIMIT` 走既有 `IX_SmsMessages_Timestamp` 索引，返回前反转为升序保持原语义）；启动路径取 500 条、远程 OTP 取 50 条；`GetAllSmsMessagesAsync` 委托全量语义不变。接口同步声明于 `IPreferenceDatabaseService`。索引（Timestamp/RemoteNumber）经核实已存在，无需新建。
- **P1.4-P1.6 审查结论（不改动）**：日志已是合批 drain（每批 100 条 + Background 优先级 + 原子去重调度 + 4096 有界 Channel 文件写入），高频 URC 下无退化；SystemStatus 心跳定时器每秒仅做 Volatile.Read 内存读取 + 11 个属性通知，无全量集合刷新；音频池化在基线无 GC 压力证据下按方案跳过。

## 稳定性（S2）

- **看门狗竞态审查**：所有 VoWiFi 恢复入口（内部事件即时上报 + 30s 巡检）统一经 `RestartVoWifiAsync` 的 `TryAdd` 原子 in-flight 门 + `TryConsumeRepair` 修复预算双防护；全量重扫描有 5 分钟全局节流。未发现竞态。
- **保活策略可测化**：崩溃自重启熔断（5 分钟 3 次）与 UI 异常熔断（1 分钟 20 次）判定提取为 `Helpers/KeepAlivePolicy` 纯函数，App.xaml.cs 接入；6 项单元测试覆盖允许/熔断/窗口滑过/坏行容错。WMI 脱盘自重启逻辑本身不变。
- **DB 损坏容错**：`PreferenceDatabaseService.InitializeAsync` 捕获 SqliteException/IOException → 坏档改名隔离 + `SqliteConnection.ClearAllPools()`（连接池残留句柄是重建失败根因）→ 重跑建表 → 仍失败则退化为共享内存库（`file:vowin-degraded?mode=memory&cache=shared` + 常驻保活连接，普通 `:memory:` 每连接独立空库不可用）。2 项测试覆盖坏文件存活与正常库不降级。
- **单实例激活链路审查**：互斥体抢锁失败 → AutoReset 事件激活旧实例 → 500ms 轮询监听 + `BeginInvoke` 投递 UI 线程恢复窗口；命名对象创建失败时 fail-closed 保护模组。未发现问题。

## 结构（C3）

- 删除 5 个无引用 `.bak_esim*` 文件（回收站可恢复）。
- `VoKernelService` 拆分为 4 个文件：主文件（初始化/看门狗/事件/短信会话/AT/USSD/偏好）、`VoKernelService.Euicc.cs`、`VoKernelService.ProxyRouting.cs`、`VoKernelService.Audio.cs`，全部 partial、同 using 集，编译 0 警告 0 错误。
- 大 VM（SystemStatus 1826 行 / ModemManager 1722 行）提取 Helper：评估后跳过——无对应性能/稳定性问题，纯结构重构风险大于收益。

## 验证

- `dotnet build`（keepalive 独立输出目录，不触碰运行中进程）：0 警告 0 错误。
- VoWin.Tests **505/505 通过**，连续 10 轮压测无失败、无波动。
- 未动用户进程：全程未杀死任何前台/后台 VoWin 实例。

## 遗留事项

1. **启动埋点对比**：受单实例互斥限制，需等当前运行实例（旧构建）从托盘退出、新构建启动后，对比 `%LOCALAPPDATA%\VoWin\Diagnostics\diagnostic_*.log` 中 OnStartup→InitializeAsync→StartWatchdogs 各段耗时与基线（~1020/720ms）。
2. **内存指标复测**：WorkingSet 217.7MB 基线复测同样依赖新实例运行。
3. qmiSharp.Tests 有 1 项环境性失败（在输出目录找不到 `qmi/references/libqmi/data` 参考数据），与本次改动无关，仅在全解决方案运行时出现。

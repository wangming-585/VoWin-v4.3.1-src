using System.Buffers.Binary;
using System.Collections.Generic;
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
    /// <summary>VoKernelService 拆分：槽位代理 / 代理连通性测试 / ICCID 与国家路由 / egress 解析（partial，与主文件同一类型）。</summary>
    public partial class VoKernelService
    {
        public bool SetSlotProxy(string slotId, string? proxyUrl)
        {
            AddLog("INFO", "Proxy", $"Setting proxy for slot {slotId}: {proxyUrl ?? "Direct"}");
            return Kernel.SetSlotProxy(slotId, proxyUrl);
        }

        public async Task<int> TestProxyConnectivityAsync(ProxyNodeModel node, string targetHost = "8.8.8.8", int targetPort = 53)
        {
            var sw = Stopwatch.StartNew();
            node.Status = "正在验证 SOCKS5 公网 UDP 收发...";
            IPEndPoint? relay = null;

            try
            {
                using var proxy = Socks5Client.TryParse(node.ToProxyUrl())
                    ?? throw new InvalidOperationException("VoWiFi 需要 socks://、socks5:// 或 socks5h:// 代理；HTTP 代理不支持 IKEv2/ESP UDP。");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                relay = await proxy.UdpAssociateAsync(cts.Token);

                var targetIp = IPAddress.TryParse(targetHost, out var parsedTarget)
                    ? parsedTarget
                    : (await Dns.GetHostAddressesAsync(targetHost, cts.Token).ConfigureAwait(false))
                        .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
                        ?? throw new InvalidOperationException($"无法解析 UDP 探测目标 {targetHost}。");

                var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue + 1);
                var dnsQuery = BuildDnsHealthQuery(transactionId);
                var dnsResponse = await proxy.UdpRoundTripAsync(
                    dnsQuery,
                    new IPEndPoint(targetIp, targetPort),
                    cts.Token).ConfigureAwait(false);
                if (dnsResponse.Length < 12 ||
                    BinaryPrimitives.ReadUInt16BigEndian(dnsResponse.AsSpan(0, 2)) != transactionId ||
                    (dnsResponse[2] & 0x80) == 0)
                {
                    throw new InvalidOperationException("SOCKS5 公网 UDP 返回了无效的 DNS 响应。");
                }

                sw.Stop();
                int ms = (int)sw.ElapsedMilliseconds;
                node.LatencyMs = ms;
                node.Status = $"SOCKS5 公网 UDP 正常 ({ms}ms)";
                AddLog("INFO", "Proxy",
                    $"SOCKS5 UDP data plane healthy for {node.Name} ({node.Host}:{node.Port}); relay {relay}, DNS {targetIp}:{targetPort}, RTT {ms}ms");
                return ms;
            }
            catch (Exception ex)
            {
                sw.Stop();
                node.LatencyMs = -1;
                node.Status = relay == null
                    ? $"SOCKS5 UDP 关联失败: {ex.Message}"
                    : $"SOCKS5 UDP 已关联，但公网 DNS 探测无响应: {ex.Message}";
                AddLog("WARN", "Proxy",
                    $"SOCKS5 UDP health check failed for {node.Name} ({node.Host}:{node.Port}); " +
                    $"relay={(relay == null ? "not established" : relay)}, error={ex.Message}");
                return -1;
            }
        }

        public async Task<string> BuildImsDiagnosticReportAsync(string? slotId = null)
        {
            var slot = (!string.IsNullOrWhiteSpace(slotId)
                ? Slots.FirstOrDefault(s => s.Id == slotId)
                : ActiveSlot) ?? Slots.FirstOrDefault();
            var diag = slot?.VoWifiDiag ?? VoWifiDiag;
            var routeDecision = await ResolveVoWifiRouteAsync(slot?.Id).ConfigureAwait(false);
            var report = new StringBuilder();
            var appVersion = typeof(VoKernelService).Assembly.GetName().Version?.ToString() ?? "unknown";

            report.AppendLine("VoWin IMS diagnostic report");
            report.AppendLine("This report automatically redacts subscriber identities, phone numbers, AKA material, and proxy credentials.");
            report.AppendLine($"Generated (local): {DateTimeOffset.Now:O}");
            report.AppendLine($"App version: {appVersion}");
            report.AppendLine($"OS: {Environment.OSVersion}");
            report.AppendLine();
            report.AppendLine("=== Selected modem / SIM context ===");
            report.AppendLine($"Slot: {slot?.Id ?? "none"}");
            report.AppendLine($"Port: {slot?.PortName ?? "unknown"}");
            report.AppendLine($"Firmware: {slot?.Modem?.FirmwareRevision ?? "unknown"}");
            report.AppendLine($"Flight mode: {slot?.IsFlightMode.ToString() ?? "unknown"}");
            report.AppendLine($"SIM PLMN: {slot?.Sim?.Mcc ?? "?"}-{slot?.Sim?.Mnc ?? "?"}");
            report.AppendLine($"IMSI source: {slot?.ImsiIdentitySource ?? "unknown"}");
            report.AppendLine($"EF_IMSI PLMN: {DescribeImsiPlmn(slot?.LastPermanentImsi)}");
            report.AppendLine($"AT+CIMI PLMN: {DescribeImsiPlmn(slot?.LastReportedImsi)}");
            report.AppendLine($"Multi-IMSI PLMN conflict: {slot?.HasImsiPlmnConflict.ToString() ?? "unknown"}");
            report.AppendLine($"Stable routing MCC: {slot?.StableRoutingMcc ?? "unknown"}");
            report.AppendLine($"SOCKS route configured: {!string.IsNullOrWhiteSpace(slot?.ProxyUrl)}");
            report.AppendLine($"Route rule: {routeDecision.RuleDisplay}");
            report.AppendLine($"Route target: {routeDecision.TargetDisplay}");
            report.AppendLine();
            report.AppendLine("=== Cellular Host IMS prerequisite probe ===");
            if (slot?.Modem is { IsOpen: true } modem)
            {
                try
                {
                    var hostIms = await modem.ProbeHostImsPdnAsync().ConfigureAwait(false);
                    report.AppendLine($"Readiness: {hostIms.Readiness}");
                    report.AppendLine($"Modem IMS setting: {hostIms.ModemImsEnabled?.ToString() ?? "MBN/default"}");
                    report.AppendLine($"USB network mode: {hostIms.UsbNetworkMode ?? "unknown"}");
                    foreach (var adapter in hostIms.WindowsCellularAdapters)
                        report.AppendLine($"Windows cellular candidate: {DiagnosticLogRedactor.Redact(adapter.Name)} " +
                            $"({(adapter.IsConnected ? "connected" : "disconnected")}; {DiagnosticLogRedactor.Redact(adapter.Description)})");
                    foreach (var candidate in hostIms.EndpointCandidates)
                        report.AppendLine($"IMS endpoint candidate: {candidate.Display}; " +
                            $"local={candidate.LocalAddress}; P-CSCF={candidate.PcscfAddress}; route=unverified");
                    report.AppendLine($"Conclusion: {hostIms.Summary}");
                    foreach (var context in hostIms.Contexts)
                    {
                        report.AppendLine(
                            $"{context.Label}: type={context.PdpType}; active={context.IsActive}; " +
                            $"local={string.Join(',', context.LocalAddresses)}; P-CSCF={string.Join(',', context.PcscfServers)}; " +
                            $"Windows interfaces={string.Join(',', context.HostInterfaces.DefaultIfEmpty("none"))}");
                    }
                }
                catch (Exception ex)
                {
                    report.AppendLine($"Probe failed: {DiagnosticLogRedactor.Redact(ex.Message)}");
                }
            }
            else report.AppendLine("Unavailable: no open physical modem.");
            report.AppendLine();
            report.AppendLine("=== VoWiFi / IMS snapshot ===");
            report.AppendLine($"State: {diag?.State.ToString() ?? "unavailable"}");
            report.AppendLine($"Last error: {DiagnosticLogRedactor.Redact(diag?.LastError ?? "none")}");
            report.AppendLine($"Failure classification: stage={diag?.FailureStage ?? "none"}; category={diag?.FailureCategory ?? "none"}");
            report.AppendLine($"ePDG: {diag?.EpdgFqdn ?? "unknown"} ({diag?.EpdgIp ?? "unknown"}:{diag?.EpdgPort})");
            report.AppendLine($"IKE suite: {diag?.Suite.ToString() ?? "unknown"}; DH: {diag?.DhGroup ?? "unknown"}; EAP: {diag?.EapMethod ?? "unknown"}");
            report.AppendLine($"Tunnel: {(diag?.Tunnel is null ? "not established" : $"assigned={diag.Tunnel.AssignedIPv4 ?? diag.Tunnel.AssignedIPv6 ?? "unknown"}; P-CSCF={diag.Tunnel.PcscfIp}; ESP={diag.Tunnel.EncryptionAlgorithm}/{diag.Tunnel.IntegrityAlgorithm}")}");
            report.AppendLine($"IMS result: {diag?.Ims?.RegistrationState ?? "not registered"}; security={diag?.Ims?.SecurityAssociation ?? "n/a"}; expiry={diag?.Ims?.ExpiresSeconds.ToString() ?? "n/a"}");
            report.AppendLine($"SIP probes: sent={diag?.SipProbesSent ?? 0}; success={diag?.SipProbesSuccess ?? 0}; failed={diag?.SipProbesFailed ?? 0}; last={diag?.LastProbeResult ?? "none"}");
            report.AppendLine();
            report.AppendLine("=== Latest IMS registration chain ===");
            report.AppendLine("Only the most recent VoWiFi start attempt and its ePDG / IKE / EAP / tunnel / IMS / SIP events are included.");

            try
            {
                var persisted = await ReadTailLinesAsync(GetLogFilePath(), 5000).ConfigureAwait(false);
                var live = Logs.Select(entry =>
                    $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}] [{entry.Source}] {entry.Message}");
                var chain = ExtractLatestImsRegistrationChain(persisted.Concat(live));
                foreach (var line in chain)
                    report.AppendLine(DiagnosticLogRedactor.Redact(line));
            }
            catch (Exception ex)
            {
                report.AppendLine($"[WARN] Could not read persisted log: {DiagnosticLogRedactor.Redact(ex.Message)}");
            }

            AddLog("INFO", "Diagnostics", "A concise IMS registration-chain diagnostic report was generated (sensitive fields redacted).");
            return report.ToString();
        }

        private static string DescribeImsiPlmn(string? imsi)
        {
            if (string.IsNullOrWhiteSpace(imsi) || imsi.Length < 5) return "unavailable";
            return $"{imsi[..3]}-{imsi.Substring(3, 2)}…";
        }

        private static IReadOnlyList<string> ExtractLatestImsRegistrationChain(IEnumerable<string> source)
        {
            var allLines = source
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var startIndex = allLines.FindLastIndex(IsImsRegistrationStart);
            // Keep the few setup events immediately before the start marker so
            // the selected SOCKS/direct route is visible with the attempt.
            var attemptLines = startIndex >= 0
                ? allLines.Skip(Math.Max(0, startIndex - 5))
                : allLines;
            var relevant = attemptLines.Where(IsImsRegistrationEvent).ToList();

            if (relevant.Count == 0)
                return ["No VoWiFi registration event was recorded yet. Reproduce the failure once, then export the report immediately."];

            // An ESP-heavy trace can be noisy. Preserve the beginning (DNS/IKE)
            // and the end (the actual failure) while keeping the report shareable.
            const int maxLines = 420;
            if (relevant.Count <= maxLines) return relevant;

            var excerpt = relevant.Take(100).ToList();
            excerpt.Add($"[INFO] [Diagnostics] {relevant.Count - maxLines} registration-chain events omitted; showing setup and final failure.");
            excerpt.AddRange(relevant.TakeLast(maxLines - excerpt.Count));
            return excerpt;
        }

        private static bool IsImsRegistrationStart(string line) =>
            line.Contains("Starting VoWiFi on slot", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("VoWiFi state: ResolvingEpdg", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("REGISTER diagnostics started", StringComparison.OrdinalIgnoreCase);

        private static bool IsImsRegistrationEvent(string line) =>
            line.Contains("[VoWiFi]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[IMS]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[SIP]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[IKE", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[IPsec", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[Proxy]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[StateMachine]", StringComparison.OrdinalIgnoreCase);

        private static async Task<IReadOnlyList<string>> ReadTailLinesAsync(string path, int maxLines)
        {
            if (!File.Exists(path)) return Array.Empty<string>();

            var lines = new Queue<string>(maxLines);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, useAsync: true);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (lines.Count == maxLines) lines.Dequeue();
                lines.Enqueue(line);
            }
            return lines.ToArray();
        }

        private static byte[] BuildDnsHealthQuery(ushort transactionId)
        {
            // Minimal recursive A query for example.com.
            var query = new byte[29];
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(0, 2), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(2, 2), 0x0100);
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(4, 2), 1);
            query[12] = 7;
            Encoding.ASCII.GetBytes("example").CopyTo(query, 13);
            query[20] = 3;
            Encoding.ASCII.GetBytes("com").CopyTo(query, 21);
            query[24] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(25, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(27, 2), 1);
            return query;
        }

        public void AddProxyPreset(ProxyNodeModel node)
        {
            ProxyPresets.Add(node);
            _ = Preferences.SaveProxyNodeAsync(node);
            AddLog("INFO", "Proxy", $"Added proxy preset: {node.Name} ({node.ToProxyUrl()})");
            RaiseEgressRoutesChanged();
        }

        public void RemoveProxyPreset(string nodeId)
        {
            var item = ProxyPresets.FirstOrDefault(p => p.Id == nodeId);
            if (item != null)
            {
                ProxyPresets.Remove(item);
                _ = Preferences.DeleteProxyNodeAsync(nodeId);
                AddLog("INFO", "Proxy", $"Removed proxy preset: {item.Name}");
                RaiseEgressRoutesChanged();
            }
        }

        public string? ResolveEgressProxyForSlot(string slotId)
        {
            var slot = Slots.FirstOrDefault(s => s.Id == slotId);
            if (slot == null) return null;

            return EgressRouteResolver.Resolve(
                slot.Sim?.Iccid,
                slot.StableRoutingMcc ?? slot.Sim?.Mcc,
                slot.Sim?.Mcc,
                slot.HasImsiPlmnConflict,
                ProxyPresets,
                IccidRoutes,
                CountryRoutes).ProxyUrl;
        }

        public async Task<VoWifiRouteDecision> ResolveVoWifiRouteAsync(string? slotId = null)
        {
            var slot = (!string.IsNullOrWhiteSpace(slotId)
                ? Slots.FirstOrDefault(candidate => string.Equals(candidate.Id, slotId, StringComparison.OrdinalIgnoreCase))
                : ActiveSlot) ?? Slots.FirstOrDefault();

            if (slot == null)
            {
                return new VoWifiRouteDecision(
                    "未选择通信设备",
                    "未解析",
                    "请先选择已识别的 SIM 卡。",
                    null,
                    false);
            }

            await Task.CompletedTask;
            var resolution = EgressRouteResolver.Resolve(
                slot.Sim?.Iccid,
                slot.StableRoutingMcc ?? slot.Sim?.Mcc,
                slot.Sim?.Mcc,
                slot.HasImsiPlmnConflict,
                ProxyPresets,
                IccidRoutes,
                CountryRoutes);
            return new VoWifiRouteDecision(
                resolution.Tier,
                resolution.TargetDisplay,
                resolution.Reason,
                resolution.ProxyUrl,
                resolution.IsUsable && resolution.ProxyUrl != null);
        }

        public void SaveIccidRoute(string iccid, string? proxyNodeId, string? cardName = null, string? imsi = null, string? phoneNumber = null)
        {
            var normalizedIccid = iccid?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedIccid))
                throw new ArgumentException("ICCID 不能为空。", nameof(iccid));

            var node = !string.IsNullOrWhiteSpace(proxyNodeId)
                ? ProxyPresets.FirstOrDefault(candidate => candidate.Id == proxyNodeId)
                : null;
            if (!string.IsNullOrWhiteSpace(proxyNodeId) && node == null)
                throw new InvalidOperationException("所选代理节点不存在或已被删除。");

            var route = IccidRoutes.FirstOrDefault(candidate =>
                string.Equals(candidate.Iccid, normalizedIccid, StringComparison.Ordinal));
            if (route == null)
            {
                route = new IccidRouteModel { Iccid = normalizedIccid };
                IccidRoutes.Add(route);
            }

            route.CardName = string.IsNullOrWhiteSpace(cardName) ? route.CardName : cardName.Trim();
            route.ImsiSnapshot = string.IsNullOrWhiteSpace(imsi) ? route.ImsiSnapshot : imsi.Trim();
            // 传入为空时保留旧值：号码只在能从活卡取到时更新，绝不因保存规则而丢号。
            route.PhoneNumber = string.IsNullOrWhiteSpace(phoneNumber) ? route.PhoneNumber : phoneNumber.Trim();
            route.ProxyNodeId = node?.Id;
            route.ProxyUrl = null;
            route.ProxyNodeName = node?.Name ?? "直连模式 (Direct)";
            route.UpdatedAt = DateTime.Now;
            _ = Preferences.SaveIccidRouteAsync(route);

            AddLog("INFO", "Proxy Routing",
                $"Updated ICCID egress rule: ****{normalizedIccid[^Math.Min(4, normalizedIccid.Length)..]} -> {node?.Name ?? "Direct"}");
            RaiseEgressRoutesChanged();
        }

        private CancellationTokenSource? _iccidIdentityRefreshCts;

        /// <summary>SIM/VoWiFi 状态变化后的防抖回填：500ms 内多次事件只触发一次。</summary>
        private void ScheduleIccidIdentityRefresh()
        {
            var cts = _iccidIdentityRefreshCts;
            if (cts != null) return; // 已有待执行的刷新。
            _iccidIdentityRefreshCts = new CancellationTokenSource();
            _ = RunIccidIdentityRefreshAsync();
        }

        private async Task RunIccidIdentityRefreshAsync()
        {
            try
            {
                await Task.Delay(500);
                RefreshIccidRouteIdentities();
            }
            catch (Exception ex) { AddLog("WARN", "Proxy Routing", $"ICCID identity refresh failed: {ex.Message}"); }
            finally
            {
                _iccidIdentityRefreshCts = null;
            }
        }

        /// <summary>
        /// 用活卡最新身份（号码/卡名/IMSI）回填已保存的 ICCID 规则行。
        /// 只在取到非空且与旧值不同的数据时写库，避免空值覆盖既有号码。
        /// </summary>
        public void RefreshIccidRouteIdentities()
        {
            var updated = 0;
            var autoAdded = 0;

            // 1) 活卡自动补行：把当前读卡器里读到的每一张卡纳入规则表，缺行时
            //    自动补一条默认直连规则。删除规则后若卡仍插入，这里会重新出现；
            //    卡拔出后对应行保留（历史可追溯）。
            var known = new HashSet<string>(IccidRoutes.Select(r => r.Iccid), StringComparer.Ordinal);
            foreach (var slot in Slots)
            {
                var iccid = slot?.Sim?.Iccid?.Trim();
                if (string.IsNullOrWhiteSpace(iccid) || known.Contains(iccid)) continue;
                var route = new IccidRouteModel
                {
                    Iccid = iccid,
                    ProxyNodeName = "直连模式 (Direct)",
                    UpdatedAt = DateTime.Now
                };
                IccidRoutes.Add(route);
                _ = Preferences.SaveIccidRouteAsync(route);
                known.Add(iccid);
                autoAdded++;
            }

            // 2) 对存在活卡的规则行回填号码/IMSI/卡名。
            foreach (var route in IccidRoutes)
            {
                var slot = Slots.FirstOrDefault(candidate =>
                    string.Equals(candidate.Sim?.Iccid?.Trim(), route.Iccid, StringComparison.Ordinal));
                if (slot?.Sim == null) continue;

                var assocUri = slot.VoWifiDiag?.Ims?.PAssociatedUri;
                var number = CardDisplayNameResolver.ResolvePhoneNumber(slot.Sim, assocUri);
                AddLog("INFO", "Proxy Routing",
                    $"Identity probe: iccid=…{route.Iccid[^4..]}, simNumber={(string.IsNullOrWhiteSpace(slot.Sim.PhoneNumber) ? "none" : $"*{slot.Sim.PhoneNumber[^4..]}")}, " +
                    $"assocUri={(string.IsNullOrWhiteSpace(assocUri) ? "none" : $"len={assocUri.Length} [{assocUri}]")}, resolved={(string.IsNullOrWhiteSpace(number) ? "none" : $"*{number[^4..]}")}");
                var changed = false;
                if (!string.IsNullOrWhiteSpace(number) && !string.Equals(route.PhoneNumber, number, StringComparison.Ordinal))
                {
                    route.PhoneNumber = number;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(slot.Sim.Imsi) && !string.Equals(route.ImsiSnapshot, slot.Sim.Imsi, StringComparison.Ordinal))
                {
                    route.ImsiSnapshot = slot.Sim.Imsi;
                    changed = true; // IMSI 变化会同时刷新国家派生列。
                }
                var cardName = !string.IsNullOrWhiteSpace(slot.CardNickname)
                    ? slot.CardNickname!
                    : CarrierDisplayHelper.BuildCardLabel(slot.Sim, slot.Sim.Iccid);
                if (!string.IsNullOrWhiteSpace(cardName) && !string.Equals(route.CardName, cardName, StringComparison.Ordinal))
                {
                    route.CardName = cardName;
                    changed = true;
                }

                if (!changed) continue;
                route.UpdatedAt = DateTime.Now;
                _ = Preferences.SaveIccidRouteAsync(route);
                updated++;
            }

            var liveSlots = Slots
                .Where(s => s?.Sim != null)
                .Select(s => $"{s.Sim!.Iccid?[^4..]}(ph={s.Sim!.PhoneNumber?[^4..] ?? "none"})")
                .ToList();
            AddLog("INFO", "Proxy Routing",
                $"Identity refresh done: rules={IccidRoutes.Count}, updated={updated}, autoAdded={autoAdded}, " +
                $"liveSlots=[{string.Join(", ", liveSlots)}], " +
                $"rulesIccids=[{string.Join(", ", IccidRoutes.Select(r => r.Iccid?[^4..]))}]");

            if (autoAdded > 0)
                AddLog("INFO", "Proxy Routing", $"Auto-added {autoAdded} live ICCID rule row(s) (default direct).");
            if (updated > 0 || autoAdded > 0)
            {
                AddLog("INFO", "Proxy Routing", $"Refreshed identity (number/IMSI/name) on {updated} ICCID rule row(s) from live SIMs.");
                RaiseEgressRoutesChanged();
            }
        }

        public void RemoveIccidRoute(string iccid)
        {
            var normalizedIccid = iccid?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedIccid)) return;

            var route = IccidRoutes.FirstOrDefault(candidate =>
                string.Equals(candidate.Iccid, normalizedIccid, StringComparison.Ordinal));
            if (route == null) return;

            IccidRoutes.Remove(route);
            _ = Preferences.DeleteIccidRouteAsync(normalizedIccid);
            AddLog("INFO", "Proxy Routing",
                $"Removed ICCID egress rule: ****{normalizedIccid[^Math.Min(4, normalizedIccid.Length)..]}; the card now follows its IMSI MCC/PLMN rule.");
            RaiseEgressRoutesChanged();
        }

        private void RaiseEgressRoutesChanged()
        {
            try { EgressRoutesChanged?.Invoke(); } catch { }
        }

        public void SaveCountryRoute(string countryCode, string? proxyNodeId)
        {
            var rule = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase));
            var node = !string.IsNullOrEmpty(proxyNodeId) ? ProxyPresets.FirstOrDefault(p => p.Id == proxyNodeId) : null;

            if (rule != null)
            {
                rule.ProxyNodeId = proxyNodeId;
                rule.ProxyNodeName = node != null ? $"{node.Name} ({node.Host}:{node.Port})" : "直连模式 (Direct)";
            }
            else
            {
                var meta = MccCountryHelper.FindByCode(countryCode);
                if (meta != null)
                {
                    rule = new CountryRouteModel
                    {
                        CountryCode = meta.Code,
                        CountryName = meta.Name,
                        FlagEmoji = meta.Flag,
                        MccList = string.Join(", ", meta.Mccs),
                        ProxyNodeId = proxyNodeId,
                        ProxyNodeName = node != null ? $"{node.Name} ({node.Host}:{node.Port})" : "直连模式 (Direct)"
                    };
                    CountryRoutes.Add(rule);
                }
            }

            if (rule != null)
            {
                _ = Preferences.SaveCountryRouteAsync(rule);
            }

            AddLog("INFO", "Proxy Routing", $"Updated country egress rule: {countryCode} -> {(node != null ? node.Name : "Direct")}");
            RaiseEgressRoutesChanged();
        }

        public void RemoveCountryRoute(string countryCode)
        {
            var rule = CountryRoutes.FirstOrDefault(r => string.Equals(r.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase));
            if (rule != null)
            {
                CountryRoutes.Remove(rule);
                _ = Preferences.DeleteCountryRouteAsync(countryCode);
                AddLog("INFO", "Proxy Routing", $"Removed country rule: {countryCode}");
                RaiseEgressRoutesChanged();
            }
        }

    }
}

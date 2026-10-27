using System.Text.RegularExpressions;
using VoSharp.Common.Utils;
using VoSharp.Modem;
using VoSharp.Sim.Pcsc;

namespace VoSharp.Euicc.Transport;

public interface IEuiccTransport
{
    string BackendName { get; }
    Task<int> OpenLogicalChannelAsync(string aid, CancellationToken ct = default);
    Task<byte[]> TransmitLogicalChannelAsync(int channel, byte[] apdu, CancellationToken ct = default);
    Task CloseLogicalChannelAsync(int channel, CancellationToken ct = default);
}

/// <summary>
/// A transport that can power-cycle the card. EnableProfile with refresh=true
/// makes the eUICC start a REFRESH procedure and wait for a terminal
/// acknowledgement; a PC/SC reader never sends one, so the card stays locked
/// and rejects every later command with 6985 until power is cycled.
/// </summary>
public interface IEuiccCardResettable
{
    void ResetCard();
}

/// <summary>
/// Implements eUICC transport over AT Modem commands (+CSIM MANAGE CHANNEL / SELECT / GET RESPONSE, with +CCHO fallback).
/// </summary>
public class AtModemEuiccTransport : IEuiccTransport
{
    private readonly ModemDriver _modem;
    private bool _useCcho = false;
    public string BackendName => _useCcho ? "AT Modem (CCHO/CGLA)" : "AT Modem (CSIM Logical Channel)";

    public AtModemEuiccTransport(ModemDriver modem)
    {
        _modem = modem;
    }

    public async Task<int> OpenLogicalChannelAsync(string aid, CancellationToken ct = default)
    {
        // 1. Primary path: MANAGE CHANNEL (open): 00 70 00 00 01 -> "<channel> 90 00"
        byte[] openChannelApdu = new byte[] { 0x00, 0x70, 0x00, 0x00, 0x01 };
        byte[]? openResp = null;

        try
        {
            openResp = await _modem.SendCsimApduAsync(openChannelApdu, ct).ConfigureAwait(false);
        }
        catch
        {
            openResp = null;
        }

        if (openResp != null && openResp.Length >= 3 && openResp[^2] == 0x90 && openResp[^1] == 0x00)
        {
            int channel = openResp[0];
            if (channel > 0)
            {
                // The caller owns AID discovery.  Falling through to another
                // ISD-R here would make a multi-eUICC card look selectable
                // while silently operating on a different storage.
                try
                {
                    var aidBytes = HexUtils.FromHexString(aid);
                    var selectApdu = new List<byte> { EncodeLogicalChannelCla(0x00, channel), 0xA4, 0x04, 0x00, (byte)aidBytes.Length };
                    selectApdu.AddRange(aidBytes);

                    var selectResp = await _modem.SendCsimApduAsync(selectApdu.ToArray(), ct).ConfigureAwait(false);
                    if (selectResp.Length >= 2)
                    {
                        int selectSw = (selectResp[^2] << 8) | selectResp[^1];
                        if (selectSw == 0x9000 || (selectSw >> 8) == 0x61)
                        {
                            if ((selectSw >> 8) == 0x61)
                            {
                                try
                                {
                                    var getResp = new byte[] { EncodeLogicalChannelCla(0x80, channel), 0xC0, 0x00, 0x00, (byte)(selectSw & 0xFF) };
                                    await _modem.SendCsimApduAsync(getResp, ct).ConfigureAwait(false);
                                }
                                catch { }
                            }
                            _useCcho = false;
                            return channel;
                        }
                    }
                }
                catch { }

                // Close the channel when this explicitly requested AID cannot be selected.
                try
                {
                    await _modem.SendCsimApduAsync(new byte[] { 0x00, 0x70, 0x80, (byte)channel, 0x00 }, ct).ConfigureAwait(false);
                }
                catch { }

                throw new InvalidOperationException("当前卡片未检测到 eUICC / eSIM (ISD-R) 应用。此卡为普通物理实体 SIM 卡，不支持电子切卡。");
            }
        }

        // 2. Fallback: AT+CCHO
        try
        {
            var resp = await _modem.Session.ExecuteCommandAsync($"AT+CCHO=\"{aid}\"", timeoutMs: 5000, ct: ct).ConfigureAwait(false);
            if (resp.Success)
            {
                foreach (var line in resp.Lines)
                {
                    var match = Regex.Match(line, @"\+CCHO:\s*(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var ch) && ch > 0)
                    {
                        _useCcho = true;
                        return ch;
                    }
                }
            }
        }
        catch { }

        throw new InvalidOperationException("模组未能建立 eUICC 逻辑通道，当前卡片可能不支持 eUICC 或通道繁忙。");
    }

    public async Task<byte[]> TransmitLogicalChannelAsync(int channel, byte[] apdu, CancellationToken ct = default)
    {
        if (channel is <= 0 or >= 20)
            throw new ArgumentOutOfRangeException(nameof(channel), "ISO 7816 logical channel must be between 1 and 19.");
        if (apdu.Length < 4)
            throw new ArgumentException("APDU is too short.", nameof(apdu));

        byte[] framed = (byte[])apdu.Clone();
        framed[0] = EncodeLogicalChannelCla(framed[0], channel);

        if (_useCcho)
        {
            var hexStr = HexUtils.ToHexString(framed);
            var resp = await _modem.Session.ExecuteCommandAsync($"AT+CGLA={channel},{hexStr.Length},\"{hexStr}\"", timeoutMs: 30000, ct: ct).ConfigureAwait(false);
            if (!resp.Success)
                throw new InvalidOperationException($"Failed to transmit APDU over channel {channel}: {string.Join(" ", resp.Lines)}");

            foreach (var line in resp.Lines)
            {
                var match = Regex.Match(line, @"\+CGLA:\s*\d+,\s*""?([0-9A-Fa-f]+)""?");
                if (match.Success)
                {
                    return HexUtils.FromHexString(match.Groups[1].Value);
                }
            }
            throw new InvalidOperationException($"Could not extract APDU response: {string.Join(" ", resp.Lines)}");
        }

        // Over CSIM: Inject channel into CLA low 2 bits
        // STORE DATA during profile installation can legitimately take longer
        // than ordinary SIM APDUs while the eUICC verifies and commits a segment.
        var csimResp = await _modem.SendCsimApduAsync(framed, ct, timeoutMs: 30000).ConfigureAwait(false);
        if (csimResp.Length < 2)
            throw new InvalidOperationException("eUICC APDU 响应数据过短");

        int sw = (csimResp[^2] << 8) | csimResp[^1];
        var payload = csimResp.Take(csimResp.Length - 2).ToList();

        // 61xx loop (GET RESPONSE)
        int guard = 0;
        while ((sw >> 8) == 0x61 && guard++ < 24)
        {
            byte len = (byte)(sw & 0xFF);
            byte[] getResp = new byte[] { EncodeLogicalChannelCla(0x80, channel), 0xC0, 0x00, 0x00, len };
            var frag = await _modem.SendCsimApduAsync(getResp, ct, timeoutMs: 30000).ConfigureAwait(false);
            if (frag.Length < 2) break;
            sw = (frag[^2] << 8) | frag[^1];
            payload.AddRange(frag.Take(frag.Length - 2));
        }

        payload.Add((byte)(sw >> 8));
        payload.Add((byte)(sw & 0xFF));
        return payload.ToArray();
    }

    public async Task CloseLogicalChannelAsync(int channel, CancellationToken ct = default)
    {
        if (channel <= 0) return;

        if (_useCcho)
        {
            try
            {
                await _modem.Session.ExecuteCommandAsync($"AT+CCHC={channel}", timeoutMs: 3000, ct: ct).ConfigureAwait(false);
            }
            catch { }
            return;
        }

        try
        {
            // MANAGE CHANNEL (close): 00 70 80 <channel> 00
            byte[] closeApdu = new byte[] { 0x00, 0x70, 0x80, (byte)channel, 0x00 };
            await _modem.SendCsimApduAsync(closeApdu, ct).ConfigureAwait(false);
        }
        catch { }
    }

    private static byte EncodeLogicalChannelCla(byte cla, int channel)
    {
        if (channel is >= 0 and < 4)
            return (byte)((cla & 0xFC) | channel);
        if (channel is >= 4 and < 20)
            return (byte)((cla & 0xB0) | 0x40 | (channel - 4));
        throw new ArgumentOutOfRangeException(nameof(channel), "ISO 7816 logical channel must be between 0 and 19.");
    }
}

/// <summary>
/// Raised when the card restarts in the middle of a command, which is what a
/// SIM REFRESH looks like from the reader side. The command may well have
/// taken effect, so the caller must verify state instead of assuming failure.
/// </summary>
public sealed class EuiccCardResetException : InvalidOperationException
{
    public EuiccCardResetException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Implements eUICC transport over Windows PC/SC smartcard interface (winscard.dll).
/// </summary>
/// <remarks>
/// An eUICC answers a command that carries response data with either 61 xx or
/// 91 xx. Both mean "read the data with GET RESPONSE"; neither is an error.
/// Treating 91 0B as a failure used to abort a profile switch that had in fact
/// succeeded, so the status word is now handled explicitly.
/// After EnableProfile the card may reset (SIM REFRESH) and every logical
/// channel disappears (6A81 / 6881 / no answer). The transport therefore
/// reconnects the card session and retries before giving up.
/// </remarks>
public class PcscEuiccTransport : IEuiccTransport, IEuiccCardResettable, IAsyncDisposable
{
    private const int MaxSessionRetries = 3;
    private const int MaxGetResponseRounds = 16;

    private readonly PcscReader _pcsc;
    private readonly string? _readerName;
    private readonly object _sync = new();
    private bool _connected;

    public string BackendName => "PC/SC SmartCard";

    /// <summary>
    /// Optional sink for every APDU exchanged with the card, formatted as
    /// "TX &lt;hex&gt; -&gt; RX &lt;hex&gt;". Diagnostic only; leave null in production.
    /// Without it, a status word such as 6D00 cannot be traced to the exact
    /// command that produced it.
    /// </summary>
    public Action<string>? ApduTrace { get; set; }

    private void Trace(string text) => ApduTrace?.Invoke(text);

    public PcscEuiccTransport(PcscReader? pcsc = null, string? readerName = null)
    {
        _pcsc = pcsc ?? new PcscReader();
        _readerName = readerName;
    }

    private void EnsureConnected()
    {
        if (_connected) return;

        var readers = _pcsc.ListReaders();
        if (readers.Length == 0)
            throw new InvalidOperationException("No PC/SC smart card readers found");

        var targetReader = _readerName ?? readers[0];
        if (!_pcsc.Connect(targetReader))
            throw new InvalidOperationException($"Failed to connect to smart card reader: {targetReader}");

        _connected = true;
    }

    /// <summary>
    /// Drops the card handle and connects again. A card that went through
    /// SIM REFRESH invalidates every logical channel, so the only way forward
    /// is a fresh card session, not a retry on the old handle.
    /// </summary>
    private void Reconnect()
    {
        lock (_sync)
        {
            _connected = false;
            // Leave the card powered: a plain reconnect is enough for a lost
            // channel and does not disturb a profile switch in progress.
            try { _pcsc.Disconnect(WinSCard.SCARD_LEAVE_CARD); } catch { }
            EnsureConnected();
        }
    }

    /// <summary>
    /// Power-cycles the card. After EnableProfile the eUICC starts a REFRESH
    /// procedure and waits for the terminal to acknowledge it; a PC/SC reader
    /// never sends that acknowledgement, so the card stays locked and answers
    /// 6985 to every STORE DATA. Removing and re-applying power ends the stale
    /// procedure, which is what a phone does by rebooting the SIM.
    /// </summary>
    public void ResetCard()
    {
        lock (_sync)
        {
            _connected = false;
            try { _pcsc.Disconnect(WinSCard.SCARD_UNPOWER_CARD); } catch { }
            EnsureConnected();
        }
    }

    private static bool IsSessionLost(byte[]? resp)
    {
        if (resp == null || resp.Length < 2) return true;
        int sw = (resp[^2] << 8) | resp[^1];
        // 6A81: function not supported (channel gone after REFRESH)
        // 6881: logical channel not supported
        return sw is 0x6A81 or 0x6881;
    }

    /// <summary>
    /// 61 xx only: xx bytes of response data are queued for GET RESPONSE.
    /// 91 xx is NOT response data. Measured on this eUICC, 91 0B is what the
    /// card returns once it has started the REFRESH procedure; asking for the
    /// "pending" bytes answers 6D00 and used to overwrite a good result code.
    /// </summary>
    private static bool HasPendingData(int sw) => (sw >> 8) == 0x61;

    /// <summary>91 xx: the card started a REFRESH and waits for a terminal response.</summary>
    private static bool IsRefreshPending(int sw) => (sw >> 8) == 0x91;

    /// <summary>
    /// One APDU. When the card session died, the APDU is replayed only if the
    /// caller says it is safe. Channel management and AID selection are
    /// idempotent; a STORE DATA that mutates the eUICC is not, so it must
    /// surface as a reset for the caller to verify instead of being re-sent.
    /// </summary>
    private byte[] TransmitWithRecovery(byte[] apdu, bool replayOnReconnect)
    {
        lock (_sync)
        {
            EnsureConnected();
            var resp = _pcsc.TransmitApdu(apdu);
            Trace($"TX {HexUtils.ToHexString(apdu)} -> RX {(resp is null ? "<null>" : HexUtils.ToHexString(resp))}");
            if (!IsSessionLost(resp)) return resp!;

            Reconnect();
            if (!replayOnReconnect)
                throw new EuiccCardResetException(
                    "卡片会话在指令执行期间失效（eUICC 已进入 SIM REFRESH 重置），未重发指令以避免重复执行。");

            resp = _pcsc.TransmitApdu(apdu);
            Trace($"TX {HexUtils.ToHexString(apdu)} -> RX {(resp is null ? "<null>" : HexUtils.ToHexString(resp))} (重连后)");
            if (resp == null)
                throw new EuiccCardResetException("PC/SC 读卡器无响应：卡片可能在 SIM REFRESH 期间被重置，或读卡器被其他进程占用。");
            return resp;
        }
    }

    /// <summary>GET RESPONSE, trying both the plain and the 0x80-based CLA.</summary>
    private byte[]? TransmitGetResponse(int channel, byte le)
    {
        // Le = 00 asks for the maximum. A card answering "61 00" means "more
        // data available", not "zero bytes", so it must not be sent as 00.
        var effectiveLe = le == 0x00 ? (byte)0x00 : le;

        // STORE DATA is issued with CLA 0x80|channel, so GET RESPONSE must use
        // the same class. Trying 0x00 first returned 6D00 on this card and the
        // error was then mistaken for a failed command.
        foreach (var cla in new[] { EncodeLogicalChannelCla(0x80, channel), EncodeLogicalChannelCla(0x00, channel) })
        {
            var apdu = new byte[] { cla, 0xC0, 0x00, 0x00, effectiveLe };
            var resp = _pcsc.TransmitApdu(apdu);
            Trace($"TX {HexUtils.ToHexString(apdu)} -> RX {(resp is null ? "<null>" : HexUtils.ToHexString(resp))} (GET RESPONSE)");
            if (resp == null || resp.Length < 2) continue;
            int sw = (resp[^2] << 8) | resp[^1];
            // 6D00 / 6E00: instruction or class not supported -> try the other CLA.
            if (sw is 0x6D00 or 0x6E00) continue;
            return resp;
        }
        return null;
    }

    public async Task<int> OpenLogicalChannelAsync(string aid, CancellationToken ct = default)
    {
        // 1. MANAGE CHANNEL - open: 00 70 00 00 01 -> "<channel> 90 00"
        //    Some eUICCs answer "<channel> 91 xx" instead; the channel is still
        //    allocated, so that status must not be read as a rejection.
        var openChannelApdu = new byte[] { 0x00, 0x70, 0x00, 0x00, 0x01 };
        byte[]? openResp = null;
        var lastSw = "(无响应)";

        for (var attempt = 1; attempt <= MaxSessionRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                openResp = TransmitWithRecovery(openChannelApdu, replayOnReconnect: true);
            }
            catch (EuiccCardResetException)
            {
                // Card is mid-reset; wait for it to come back before the next try.
                openResp = null;
            }

            if (openResp is { Length: >= 2 })
            {
                int sw = (openResp[^2] << 8) | openResp[^1];
                lastSw = HexUtils.ToHexString(openResp);
                if (sw == 0x9000 || HasPendingData(sw) || IsRefreshPending(sw))
                {
                    int channel = openResp[0];
                    if (channel > 0)
                    {
                        if (IsRefreshPending(sw))
                        {
                            // The channel was allocated, but the card is in its
                            // REFRESH window: nothing can be selected reliably.
                            SafeFire.Run(CloseLogicalChannelAsync(channel, ct));
                            throw new EuiccCardResetException(
                                $"卡片在分配通道时返回 91 {sw & 0xFF:X2}，说明 eUICC 仍处于 SIM REFRESH 流程中，需断电复位。");
                        }
                        return OpenAndSelect(aid, channel, ct);
                    }
                }
            }

            if (attempt < MaxSessionRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
                // 关键：普通的 Reconnect 用的是 SCARD_LEAVE_CARD，只丢掉逻辑通道，
                // 解不开 SIM REFRESH 的锁。卡片会一直回 91 0B / 6985，直到掉电。
                // 这里必须断电复位（SCARD_UNPOWER_CARD），等同于手机重启 SIM。
                try
                {
                    ResetCard();
                }
                catch { }
            }
        }

        throw new EuiccCardResetException(
            $"无法在卡片上建立 eUICC 逻辑通道（SW={lastSw}）。卡片可能仍处于 SIM REFRESH 重置过程中，或已被其他进程独占。");
    }

    private int OpenAndSelect(string aid, int channel, CancellationToken ct)
    {
        // 2. SELECT AID on the allocated channel
        var aidBytes = HexUtils.FromHexString(aid);
        var selectApdu = new List<byte> { EncodeLogicalChannelCla(0x00, channel), 0xA4, 0x04, 0x00, (byte)aidBytes.Length };
        selectApdu.AddRange(aidBytes);

        byte[]? selectResp = null;
        try
        {
            selectResp = TransmitWithRecovery(selectApdu.ToArray(), replayOnReconnect: true);
        }
        catch (EuiccCardResetException)
        {
            // Fall through: report the selection failure below with the card state.
        }

        if (selectResp != null && selectResp.Length >= 2)
        {
            int sw = (selectResp[^2] << 8) | selectResp[^1];
            if (sw == 0x9000)
                return channel;
            if (HasPendingData(sw))
            {
                // SELECT answered 61 xx: the FCI is still queued on the card.
                // Leaving it unread desynchronises every later command, because
                // the next GET RESPONSE would return this stale FCI.
                DrainPendingData(channel, sw);
                return channel;
            }
            if (IsRefreshPending(sw))
            {
                // The card is mid-REFRESH; the channel exists but is unusable
                // until power is cycled. Surface it instead of failing silently.
                SafeFire.Run(CloseLogicalChannelAsync(channel, ct));
                throw new EuiccCardResetException(
                    $"SELECT 返回 91 {sw & 0xFF:X2}：eUICC 处于 SIM REFRESH 状态，需断电复位后再操作。");
            }
        }

        SafeFire.Run(CloseLogicalChannelAsync(channel, ct));
        throw new InvalidOperationException(
            $"PC/SC card rejected selecting AID {aid} on channel {channel}: SW={HexUtils.ToHexString(selectResp ?? [])}");
    }

    /// <summary>
    /// Reads and discards whatever the card still holds for this channel, so
    /// the next command starts from a clean response buffer.
    /// </summary>
    private void DrainPendingData(int channel, int sw)
    {
        var rounds = 0;
        while (HasPendingData(sw) && rounds++ < MaxGetResponseRounds)
        {
            var frag = TransmitGetResponse(channel, (byte)(sw & 0xFF));
            if (frag == null || frag.Length < 2) return;
            int fragSw = (frag[^2] << 8) | frag[^1];
            if (fragSw is 0x6D00 or 0x6E00) return;
            sw = fragSw;
        }
    }

    public Task<byte[]> TransmitLogicalChannelAsync(int channel, byte[] apdu, CancellationToken ct = default)
    {
        if (apdu.Length < 4) throw new ArgumentException("APDU is too short.", nameof(apdu));
        var framed = (byte[])apdu.Clone();
        framed[0] = EncodeLogicalChannelCla(framed[0], channel);

        byte[] resp;
        try
        {
            resp = TransmitWithRecovery(framed, replayOnReconnect: false);
        }
        catch (EuiccCardResetException)
        {
            throw new EuiccCardResetException(
                "指令发出后卡片失去响应：eUICC 很可能已执行该指令并进入 SIM REFRESH 重置，需通过重新读取卡片状态判定结果。");
        }

        int sw = (resp[^2] << 8) | resp[^1];
        var payload = resp[..^2].ToList();

        // Collect 61 xx data so the caller receives the real result code.
        var round = 0;
        while (HasPendingData(sw) && round++ < MaxGetResponseRounds)
        {
            var frag = TransmitGetResponse(channel, (byte)(sw & 0xFF));
            if (frag == null || frag.Length < 2) break;
            int fragSw = (frag[^2] << 8) | frag[^1];
            // A failing GET RESPONSE must not clobber a status word that already
            // describes a completed command. Keep the previous one and stop.
            if (fragSw is 0x6D00 or 0x6E00) break;
            payload.AddRange(frag[..^2]);
            sw = fragSw;
        }

        payload.Add((byte)(sw >> 8));
        payload.Add((byte)(sw & 0xFF));
        return Task.FromResult(payload.ToArray());
    }

    public Task CloseLogicalChannelAsync(int channel, CancellationToken ct = default)
    {
        if (_connected && channel > 0)
        {
            try
            {
                lock (_sync)
                {
                    // MANAGE CHANNEL - close: 00 70 80 <channel> 00
                    var closeApdu = new byte[] { 0x00, 0x70, 0x80, (byte)channel, 0x00 };
                    _pcsc.TransmitApdu(closeApdu);
                }
            }
            catch { }
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_connected)
        {
            _pcsc.Dispose();
            _connected = false;
        }
        return ValueTask.CompletedTask;
    }

    private static byte EncodeLogicalChannelCla(byte cla, int channel)
    {
        if (channel is >= 0 and < 4)
            return (byte)((cla & 0xFC) | channel);
        if (channel is >= 4 and < 20)
            return (byte)((cla & 0xB0) | 0x40 | (channel - 4));
        throw new ArgumentOutOfRangeException(nameof(channel), "ISO 7816 logical channel must be between 0 and 19.");
    }
}

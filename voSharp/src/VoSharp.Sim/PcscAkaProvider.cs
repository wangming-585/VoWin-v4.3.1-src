using VoSharp.Common.Aka;
using VoSharp.Sim.Pcsc;

namespace VoSharp.Sim;

/// <summary>Answers USIM AKA challenges through a PC/SC smartcard reader.</summary>
public sealed class PcscAkaProvider : IAkaProvider, ISmsCenterProvider, IDisposable
{
    /// <summary>USIM application AID (3GPP TS 31.102).</summary>
    private const string UsimAid = "A0000000871002";

    private readonly PcscReader _reader;
    private bool _disposed;

    // A card is a single shared resource, and every operation here is a
    // multi-APDU sequence (SELECT ADF -> SELECT EF -> READ BINARY). Two
    // callers interleaving on the same reader corrupt each other's state:
    // a read that follows someone else's SELECT returns the wrong file, the
    // ICCID comes back mismatched and the slot is wrongly torn down to
    // Offline. The watchdog probe and the VoWiFi start are exactly such a
    // pair, so the whole sequence must be atomic. Monitor is re-entrant,
    // which lets CheckReadyAsync call ReadIccidAsync under the same lock.
    private readonly object _cardLock = new();

    public PcscAkaProvider(string? readerName = null)
    {
        _reader = new PcscReader();

        // Every failure path below must release the PC/SC context. The L2
        // watchdog re-attaches the reader every 15s; a leaked context per
        // attempt would exhaust the reader's handle table and keep the slot
        // permanently offline.
        try
        {
            if (!_reader.Initialize())
                throw new InvalidOperationException(
                    "Could not establish a PC/SC context. Is the Smart Card service running?");

            var readers = _reader.ListReaders();
            if (readers.Length == 0)
                throw new InvalidOperationException("No PC/SC readers found.");

            var target = string.IsNullOrWhiteSpace(readerName) ? readers[0] : readerName;
            if (!_reader.Connect(target))
                throw new InvalidOperationException($"Could not connect to PC/SC reader '{target}'.");

            ReaderName = target;
        }
        catch
        {
            try { _reader.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Power-cycles the card and reconnects to the reader.
    /// </summary>
    /// <remarks>
    /// Needed when the card is stuck in a SIM REFRESH procedure after an eUICC
    /// profile switch: the card keeps answering 6985 / 91 0B until power is
    /// removed and re-applied. Used by the attach path so a slot that failed to
    /// read its card can recover without waiting for the operator to re-plug
    /// the reader.
    /// </remarks>
    public bool PowerCycleCard()
    {
        lock (_cardLock)
        {
            return _reader.PowerCycleAndReconnect(ReaderName);
        }
    }

    /// <summary>
    /// Reopens the card session without touching its power state.
    /// </summary>
    /// <remarks>
    /// An eUICC profile switch power-cycles the card through the eUICC transport's
    /// own handle. That invalidates this provider's handle too - every APDU then
    /// comes back as "no response" - even though the card itself is healthy and
    /// already on the new profile. Reconnecting recovers it without restarting
    /// another REFRESH cycle.
    /// </remarks>
    public bool ReconnectCard()
    {
        lock (_cardLock)
        {
            return _reader.Reconnect(ReaderName);
        }
    }

    public string ReaderName { get; }

    /// <summary>Reads EF.ICCID directly from the UICC, without relying on a modem cache.</summary>
    public Task<string> ReadIccidAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_cardLock)
        {
            SelectFile("3F00");
            SelectFile("2FE2");
            var data = ReadBinary(10);
            var iccid = DecodeBcd(data);
            if (iccid.Length is < 18 or > 22)
                throw new InvalidOperationException("EF.ICCID returned an invalid value.");
            return Task.FromResult(iccid);
        }
    }

    /// <summary>Reads EF.IMSI from ADF.USIM. This is the identity used by EAP-AKA.</summary>
    public Task<string> ReadImsiAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_cardLock)
        {
            SelectUsimApplication();
            SelectFile("6F07");
            return Task.FromResult(ReadImsiUnlocked());
        }
    }

    private string ReadImsiUnlocked()
    {
        var data = ReadBinary(9);
        if (data.Length < 2)
            throw new InvalidOperationException("EF.IMSI returned no data.");

        // The first octet is the IMSI length in BCD octets. The remaining digits
        // use the same low-nibble-first encoding as EF.ICCID.
        var byteCount = Math.Min(data[0], data.Length - 1);
        var imsi = DecodeBcd(data.AsSpan(1, byteCount));
        // 诊断：EF.IMSI 原始字节与解码结果对照。历史上出现过前导 '9' 污染
        // （长度 octet 被当作 BCD 数字解码），保留原始 hex 以定位错位来源。
        System.Diagnostics.Trace.WriteLine($"[EF.IMSI] raw={Convert.ToHexString(data)} lenOctet={data[0]} decoded={imsi}");
        if (imsi.Length is < 5 or > 16)
            throw new InvalidOperationException("EF.IMSI returned an invalid value.");
        return imsi;
    }

    public Task<bool> CheckReadyAsync(string? expectedIccid = null, CancellationToken ct = default)
    {
        lock (_cardLock)
        {
            return Task.FromResult(CheckReadyUnlocked(expectedIccid, ct));
        }
    }

    private bool CheckReadyUnlocked(string? expectedIccid, CancellationToken ct)
    {
        try
        {
            SelectUsimApplication();
            // In a PC/SC reader a physical card can be exchanged without any
            // modem URC. When a caller has a live identity, compare EF.ICCID so
            // an EAP-AKA attempt can never silently use the replacement card.
            if (!string.IsNullOrWhiteSpace(expectedIccid))
            {
                var actualIccid = ReadIccidAsync(ct).GetAwaiter().GetResult();
                if (!string.Equals(NormalizeDigits(actualIccid), NormalizeDigits(expectedIccid), StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public Task<AkaResult> AuthenticateAsync(AkaChallenge challenge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        byte[]? response;
        // The ADF selection and the AUTHENTICATE that depends on it must
        // not be split by another caller's SELECT.
        lock (_cardLock)
        {
            SelectUsimApplication();

            var apdu = HardwareAka.BuildAuthenticateApdu(challenge.Rand, challenge.Autn);
            response = HardwareAka.TransmitWithChaining(_reader.TransmitApdu, apdu, cla: 0x00);
        }

        if (response is null || response.Length == 0)
            return Task.FromResult(AkaResult.Failed("Card returned no APDU response for USIM AUTHENTICATE."));

        return Task.FromResult(HardwareAka.ParseAuthenticateResponse(response).ToAkaResult());
    }

    /// <summary>EF.SMSP — Short message service parameters (3GPP TS 31.102 §4.2.27).</summary>
    private const string EfSmsp = "6F42";

    /// <summary>
    /// Reads the TS-Service Centre Address out of EF.SMSP record 1.
    /// </summary>
    /// <remarks>
    /// A PC/SC reader has no AT channel, so AT+CSCA? is unavailable and this file is the
    /// only authoritative source for the SMSC. The record layout (TS 31.102, record length
    /// as reported by the FCP) is: alpha_id, one parameter-indicator octet, then the fixed
    /// 12-byte TP-Destination Address, 12-byte TS-Service Centre Address, TP-PID, TP-DCS
    /// and TP-VP. The parameter indicators are stored inverted: after complementing, bit 1
    /// says whether the service centre address is provisioned.
    /// </remarks>
    public Task<string?> ReadSmsCenterAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_cardLock)
        {
            return Task.FromResult(ReadSmsCenterUnlocked(ct));
        }
    }

    private string? ReadSmsCenterUnlocked(CancellationToken ct)
    {
        SelectUsimApplication();

        // Ask for the FCP so the record length comes from the card instead of a
        // hard-coded 40: UICCs are free to provision a longer alpha_id.
        var fcp = TransmitChain([0x00, 0xA4, 0x00, 0x04, 0x02, 0x6F, 0x42]);
        EnsureSuccess(fcp, $"selecting EF {EfSmsp}");
        var recordLength = ParseRecordLength(fcp!) ?? 40;

        var record = TransmitChain([0x00, 0xB2, 0x01, 0x04, (byte)Math.Min(recordLength, 255)]);
        EnsureSuccess(record, $"reading EF {EfSmsp} record 1");

        ct.ThrowIfCancellationRequested();
        return ParseServiceCentreAddress(record![..^2], recordLength);
    }

    /// <summary>
    /// Extracts the record length from a linear-fixed EF FCP. Tag 0x82 carries the file
    /// descriptor; for a fixed-length record file its last two octets before the record
    /// count are the record length (TS 102 221 §11.1.1.4.3).
    /// </summary>
    /// <summary>
    /// Reads the subscriber number from EF_MSISDN (6F40). Many MVNO/data SIMs leave
    /// this file empty or absent; a null return simply means the card does not
    /// provision an on-card number.
    /// </summary>
    public Task<string?> ReadMsisdnAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_cardLock)
        {
            return Task.FromResult(ReadMsisdnUnlocked(ct));
        }
    }

    private string? ReadMsisdnUnlocked(CancellationToken ct)
    {
        SelectUsimApplication();
        return ReadMsisdnAfterUsimUnlocked(ct);
    }

    /// <summary>
    /// Reads EF_MSISDN when ADF.USIM is already selected (caller must hold
    /// the card lock and have selected the USIM application). Avoids a second
    /// SELECT ADF.USIM inside a combined attach sequence.
    /// </summary>
    private string? ReadMsisdnAfterUsimUnlocked(CancellationToken ct)
    {
        var fcp = TransmitChain([0x00, 0xA4, 0x00, 0x04, 0x02, 0x6F, 0x40]);
        EnsureSuccess(fcp, "selecting EF 6F40 (MSISDN)");
        var recordLength = ParseRecordLength(fcp!) ?? 40;

        var record = TransmitChain([0x00, 0xB2, 0x01, 0x04, (byte)Math.Min(recordLength, 255)]);
        EnsureSuccess(record, "reading EF 6F40 record 1");

        ct.ThrowIfCancellationRequested();
        return ParseMsisdnRecord(record![..^2]);
    }

    /// <summary>

    /// <summary>
    /// Heuristic sanity check for an SPN brand string. Rejects repetitive
    /// padding / low-information garbage (e.g. EF.SPN regions left as 0xFF
    /// or repeated accents) that otherwise decode into a meaningless label
    /// like "àààà…¥". A real network brand is short and mostly ASCII letters.
    /// </summary>
    private static bool IsPlausibleOperatorName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > 32) return false;
        var control = text.Any(c => c < 0x20);
        if (control) return false;
        var distinct = text.Distinct().Count();
        if (text.Length >= 4 && distinct <= Math.Max(2, text.Length / 4)) return false;
        var asciiLetters = text.Count(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
        var nonAscii = text.Count(c => c >= 0x80);
        if (nonAscii > 0.6 * text.Length && asciiLetters == 0) return false;
        return true;
    }

    /// Reads the Service Provider Name from EF.SPN (6F46, transparent EF under
    /// ADF.USIM). Per 3GPP TS 31.102 §4.2.29 the first octet holds the display
    /// condition and character set; the remainder is the SPN string in either
    /// UCS2 or the GSM 7-bit default alphabet. A null return means the card
    /// does not provision SPN or it is unreadable.
    /// </summary>
    public Task<string?> ReadSpnAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_cardLock)
        {
            return Task.FromResult(ReadSpnAfterUsimUnlocked(ct));
        }
    }

    /// <summary>
    /// Reads EF.SPN when ADF.USIM is already selected (caller must hold the
    /// card lock). Requests up to 255 bytes; the T=0 layer retries with the
    /// card's actual length on SW=6Cxx.
    /// </summary>
    private string? ReadSpnAfterUsimUnlocked(CancellationToken ct)
    {
        SelectFile("6F46");
        var data = ReadBinary(0xFF);
        ct.ThrowIfCancellationRequested();
        return DecodeSpn(data);
    }

    /// <summary>Decodes EF.SPN content per 3GPP TS 23.038 / TS 31.102.</summary>
    private static string? DecodeSpn(byte[] data)
    {
        if (data.Length == 0) return null;
        var coding = (data[0] >> 3) & 0x03;   // bits 3..4: 0=GSM 7-bit, 1=UCS2
        var payload = data.AsSpan(1);
        if (payload.Length == 0) return null;

        if (coding == 0x01)
        {
            if ((payload.Length & 1) != 0) payload = payload[..(payload.Length - 1)];
            var text = System.Text.Encoding.BigEndianUnicode.GetString(payload).Trim('\0', ' ', '\uFFFF');
            return IsPlausibleOperatorName(text) ? text : null;
        }

        var gsm = DecodeGsm7Bit(payload);
        return IsPlausibleOperatorName(gsm) ? gsm : null;
    }

    /// <summary>Unpacks the GSM 7-bit default alphabet (TS 23.038) into Unicode.</summary>
    private static string? DecodeGsm7Bit(ReadOnlySpan<byte> data)
    {
        var text = new System.Text.StringBuilder(data.Length);
        int index = 0, shift = 0;
        while (index < data.Length)
        {
            int v = data[index] >> shift;
            if (index + 1 < data.Length) v |= data[index + 1] << (8 - shift);
            if (shift > 1 && index + 2 < data.Length) v |= data[index + 2] << (16 - shift);
            v &= 0x7F;
            if (v == 0) break;               // NUL padding terminates
            if (v == 0x1B) { shift += 7; if (shift >= 8) { shift -= 8; index++; } continue; }
            text.Append(Gsm7BitToUnicode(v));
            shift += 7;
            if (shift >= 8) { shift -= 8; index++; }
        }
        var result = text.ToString().Trim(' ');
        return result.Length == 0 ? null : result;
    }

    /// <summary>Maps a GSM 7-bit default-alphabet code point to Unicode (TS 23.038).</summary>
    private static char Gsm7BitToUnicode(int c) => c switch
    {
        0x00 => '@', 0x01 => '£', 0x02 => '$', 0x03 => '¥',
        0x04 => 'è', 0x05 => 'é', 0x06 => 'ù', 0x07 => 'ì',
        0x08 => 'ò', 0x09 => 'Ç', 0x0A => '\n', 0x0B => 'Ø',
        0x0C => 'ø', 0x0D => '\r', 0x0E => 'Å', 0x0F => 'å',
        0x10 => 'Δ', 0x11 => '_', 0x12 => 'Φ', 0x13 => 'Γ',
        0x14 => 'Λ', 0x15 => 'Ω', 0x16 => 'Π', 0x17 => 'Ψ',
        0x18 => 'Σ', 0x19 => 'Θ', 0x1A => 'Ξ', 0x1C => 'Æ',
        0x1D => 'æ', 0x1E => 'ß', 0x1F => 'É',
        0x5B => 'Ä', 0x5C => 'Ö', 0x5D => 'Ñ', 0x5E => 'Ü',
        0x5F => '§', 0x60 => '¿', 0x7B => 'ä', 0x7C => 'ö',
        0x7D => 'ñ', 0x7E => 'ü', 0x7F => 'à',
        _ => (char)c,
    };
    /// locked session with one ADF selection. This replaces the previous
    /// four separate calls (ReadIccidAsync / ReadImsiAsync / ReadMsisdnAsync
    /// / CheckReadyAsync) that re-selected the USIM application and re-read
    /// EF.ICCID several times per attach, which dominated attach latency.
    /// </summary>
    public Task<(string Iccid, string Imsi, string? Msisdn, string? Spn)> ReadIdentityAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_cardLock)
        {
            // EF.ICCID lives in MF (3F00 -> 2FE2), outside the USIM ADF.
            SelectFile("3F00");
            SelectFile("2FE2");
            var iccidData = ReadBinary(10);
            var iccid = DecodeBcd(iccidData);
            if (iccid.Length is < 18 or > 22)
                throw new InvalidOperationException("EF.ICCID returned an invalid value.");

            // EF.IMSI / EF_MSISDN / EF.SPN 均在 ADF.USIM 下；只选一次。
            SelectUsimApplication();
            SelectFile("6F07");
            var imsi = ReadImsiUnlocked();

            string? msisdn = null;
            try { msisdn = ReadMsisdnAfterUsimUnlocked(ct); }
            catch { /* EF_MSISDN 不存在或未 provision 是常态，忽略 */ }

            string? spn = null;
            try { spn = ReadSpnAfterUsimUnlocked(ct); }
            catch { /* EF.SPN 缺失/不可读时回落品牌表与 PLMN 占位 */ }

            return Task.FromResult((iccid, imsi, msisdn, spn));
        }
    }

    /// <summary>
    /// Lightweight liveness probe that only verifies the USIM application is
    /// reachable. Unlike CheckReadyAsync it never re-reads EF.ICCID, so a
    /// telemetry refresh (RefreshMetricsAsync) no longer pays for a full card
    /// read. Returns false when the card/adf is absent or unresponsive.
    /// </summary>
    public Task<bool> IsUsimReachableAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_cardLock)
        {
            try { SelectUsimApplication(); return Task.FromResult(true); }
            catch (InvalidOperationException) { return Task.FromResult(false); }
        }
    }

    /// <summary>
    /// EF_MSISDN record = alpha identifier (record length - 14) + length byte +
    /// TON/NPI + 10 BCD digits. An all-0xFF or zero-length address means empty.
    /// </summary>
    private static string? ParseMsisdnRecord(byte[] record)
    {
        var alphaLength = Math.Max(0, record.Length - 14);
        if (record.Length < alphaLength + 2) return null;

        var length = record[alphaLength];
        if (length is 0 or 0xFF) return null;
        // length counts TON/NPI + digits; at least one digit byte must follow.
        if (length < 2) return null;

        var tonNpi = record[alphaLength + 1];
        var digitBytes = Math.Min(length - 1, 10);
        var digits = DecodeBcd(record.AsSpan(alphaLength + 2, digitBytes));
        if (digits.Length == 0) return null;

        var typeOfNumber = (tonNpi >> 4) & 0x07;
        return typeOfNumber == 0x01 ? "+" + digits : digits;
    }

    private static int? ParseRecordLength(byte[] fcp)
    {
        // fcp still carries the trailing SW1/SW2.
        var body = fcp.AsSpan(0, fcp.Length - 2);
        for (var i = 0; i + 1 < body.Length; i++)
        {
            if (body[i] != 0x82) continue;
            var length = body[i + 1];
            if (length < 5 || i + 2 + length > body.Length) continue;
            var descriptor = body.Slice(i + 2, length);
            // descriptor[0] & 0x07 == 0x02 marks a linear-fixed EF.  The layout is
            // file-descriptor byte, data-coding byte, two-byte record length, record
            // count — so a 40-byte record file reads 42 21 00 28 02.
            if ((descriptor[0] & 0x07) != 0x02) continue;
            var value = (descriptor[2] << 8) | descriptor[3];
            if (value > 0) return value;
        }
        return null;
    }

    /// <summary>
    /// Decodes the TS-Service Centre Address field of an EF.SMSP record into E.164.
    /// </summary>
    private static string? ParseServiceCentreAddress(byte[] record, int recordLength)
    {
        var alphaLength = Math.Max(0, recordLength - 28);
        if (record.Length < alphaLength + 1)
            throw new InvalidOperationException("EF.SMSP record is shorter than its alpha identifier.");

        var indicators = (byte)~record[alphaLength];
        var serviceCentreProvisioned = (indicators & 0x02) != 0;
        if (!serviceCentreProvisioned) return null;

        // The address fields are fixed-size even when their indicator is clear, so the
        // service centre address always sits 12 bytes past the TP-Destination Address.
        var offset = alphaLength + 1 + 12;
        if (record.Length < offset + 12)
            throw new InvalidOperationException("EF.SMSP record is truncated before the service centre address.");

        var length = record[offset];
        // 0x00 and 0xFF both mean "no address stored".
        if (length is 0 or 0xFF) return null;

        var tonNpi = record[offset + 1];
        var digits = DecodeBcd(record.AsSpan(offset + 2, Math.Min(length - 1, 10)));
        if (digits.Length == 0) return null;

        // type_of_number lives in bits 6..4 of the TON/NPI octet; 001 = international.
        var typeOfNumber = (tonNpi >> 4) & 0x07;
        return typeOfNumber == 0x01 ? "+" + digits : digits;
    }

    /// <summary>
    /// SELECT by ADF.USIM AID. Most UICCs default to a different application, and issuing
    /// AUTHENTICATE against the wrong one yields the misleading status word 0x9862.
    /// </summary>
    private void SelectUsimApplication()
    {
        var aid = Convert.FromHexString(UsimAid);

        var select = new byte[5 + aid.Length + 1];
        select[0] = 0x00;                  // CLA
        select[1] = 0xA4;                  // INS: SELECT
        select[2] = 0x04;                  // P1: select by name
        select[3] = 0x04;                  // P2: first/only occurrence, return FCI
        select[4] = (byte)aid.Length;      // Lc
        aid.CopyTo(select, 5);
        select[^1] = 0x00;                 // Le

        var response = TransmitChain(select);
        if (response is null || response.Length < 2)
            throw new InvalidOperationException("No response while selecting ADF.USIM.");

        var sw = (ushort)((response[^2] << 8) | response[^1]);
        if (sw != 0x9000 && response[^2] != 0x61 && response[^2] != 0x9F)
            throw new InvalidOperationException(
                $"Selecting ADF.USIM failed (SW=0x{sw:X4}). Is a USIM inserted?");
    }

    private void SelectFile(string fileId)
    {
        var file = Convert.FromHexString(fileId);

        // P2 = 0x0C asks the card not to return FCP. With P2 = 0x04 many UICCs
        // answer SW = 61xx and that pending response has to be drained with
        // GET RESPONSE before the next command is accepted.
        var response = TransmitChain([0x00, 0xA4, 0x00, 0x0C, 0x02, file[0], file[1]]);
        EnsureSuccess(response, $"selecting EF {fileId}");
    }

    private byte[] ReadBinary(byte length)
    {
        var response = TransmitChain([0x00, 0xB0, 0x00, 0x00, length]);
        EnsureSuccess(response, "reading transparent EF");
        return response![..^2];
    }

    /// <summary>
    /// Sends one APDU and satisfies the T = 0 protocol rules: SW = 61xx is drained
    /// with GET RESPONSE, and SW = 6Cxx is retried once with the Le the card asked for.
    /// </summary>
    private byte[]? TransmitChain(byte[] apdu)
    {
        var response = TransmitOnce(apdu);
        if (response is { Length: >= 2 } && response[^2] == 0x6C)
        {
            var retry = new byte[apdu.Length + 1];
            apdu.CopyTo(retry, 0);
            retry[^1] = response[^1];
            response = TransmitOnce(retry);
        }
        return response;
    }

    /// <summary>
    /// Sends one APDU, reconnecting the card handle once if the reader reports
    /// no answer. An eSIM profile switch power-cycles the card to clear the SIM
    /// REFRESH lock, and that invalidates the handle this provider connected
    /// with, so every later read (EF.ICCID / EF.IMSI / EF.SMSP) would fail with
    /// "No APDU response" until the handle is re-established.
    /// </summary>
    private byte[]? TransmitOnce(byte[] apdu)
    {
        var response = HardwareAka.TransmitWithChaining(_reader.TransmitApdu, apdu, cla: apdu[0]);
        if (response is not null) return response;

        try { _reader.Disconnect(WinSCard.SCARD_LEAVE_CARD); } catch { }
        if (!_reader.Connect(ReaderName)) return null;
        return HardwareAka.TransmitWithChaining(_reader.TransmitApdu, apdu, cla: apdu[0]);
    }

    private static void EnsureSuccess(byte[]? response, string operation)
    {
        if (response is null || response.Length < 2)
            throw new InvalidOperationException($"No APDU response while {operation}.");
        var sw = (ushort)((response[^2] << 8) | response[^1]);
        if (sw != 0x9000)
            throw new InvalidOperationException($"PC/SC card rejected {operation} (SW=0x{sw:X4}).");
    }

    private static string DecodeBcd(ReadOnlySpan<byte> bytes)
    {
        var digits = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            AppendBcd(value & 0x0F, digits);
            AppendBcd(value >> 4, digits);
        }
        return digits.ToString();

        static void AppendBcd(int nibble, System.Text.StringBuilder output)
        {
            if (nibble is >= 0 and <= 9) output.Append((char)('0' + nibble));
            // 0xF is the normal padding nibble; other values are not digits.
        }
    }

    private static string NormalizeDigits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader.Dispose();
    }
}

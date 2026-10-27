using System.Text.RegularExpressions;
using VoSharp.Common.Utils;
using VoSharp.Euicc.Asn1;
using VoSharp.Euicc.Models;
using VoSharp.Euicc.Transport;

namespace VoSharp.Euicc.Sgp22;

/// <summary>
/// Client implementing the GSMA SGP.22 ES10 (LPA to eUICC interface) profile management subset.
/// Supports APDU STORE DATA commands over logical channels for ES10.GetProfilesInfo,
/// ES10.EnableProfile, ES10.DisableProfile, ES10.DeleteProfile, ES10.SetNickname, and ES10.GetEuiccInfo.
/// </summary>
public class Sgp22Client
{
    public const string IsdrAidStandard = "A0000005591010FFFFFFFF8900000100";
    public const string IsdrAidTest = "A0000005591010FFFFFFFF8900000101";

    public const uint TagGetProfilesInfo = 0xBF2D;
    public const uint TagProfileInfo = 0xE3;
    public const uint TagEnableProfile = 0xBF31;
    public const uint TagDisableProfile = 0xBF32;
    public const uint TagDeleteProfile = 0xBF33;
    public const uint TagSetNickname = 0xBF29;
    public const uint TagGetEuiccInfo1 = 0xBF20;
    public const uint TagGetEuiccInfo2 = 0xBF22;

    public const uint TagEID = 0x5A;
    public const uint TagICCID = 0x5A;
    public const uint TagISDPAID = 0x4F;
    public const uint TagProfileState = 0x9F70;
    public const uint TagProfileNickname = 0x90;
    public const uint TagServiceProviderName = 0x91;
    public const uint TagProfileName = 0x92;
    public const uint TagIconType = 0x93;
    public const uint TagProfileClass = 0x95;
    public const uint TagResultCode = 0x80;

    private readonly IEuiccTransport _transport;
    private readonly int _channel;

    public Sgp22Client(IEuiccTransport transport, int channel)
    {
        _transport = transport;
        _channel = channel;
    }

    private byte[] BuildStoreDataApdu(byte[] payload, bool includeLe = true)
    {
        byte cla = (byte)(0x80 | (_channel & 0x03));
        var apdu = new List<byte> { cla, 0xE2, 0x91, 0x00, (byte)payload.Length };
        apdu.AddRange(payload);
        if (includeLe)
        {
            apdu.Add(0x00); // Le=00 per ISO 7816 Case 4 / GSMA SGP.22
        }
        return apdu.ToArray();
    }

    private async Task<Tlv> TransmitCommandAsync(Tlv requestTlv, CancellationToken ct = default)
    {
        var reqBytes = requestTlv.Encode();
        var apdu = BuildStoreDataApdu(reqBytes, includeLe: true);

        byte[] respBytes;
        try
        {
            respBytes = await _transport.TransmitLogicalChannelAsync(_channel, apdu, ct).ConfigureAwait(false);
        }
        catch (EuiccCardResetException)
        {
            // The card reset while handling the command. Re-sending the same
            // command could commit the change twice, so let the caller verify
            // the card state instead of blindly retrying.
            throw;
        }
        catch (Exception)
        {
            // Retry without Le for cards with non-standard APDU case handling
            apdu = BuildStoreDataApdu(reqBytes, includeLe: false);
            respBytes = await _transport.TransmitLogicalChannelAsync(_channel, apdu, ct).ConfigureAwait(false);
        }

        if (respBytes.Length < 2)
            throw new InvalidOperationException("eUICC APDU response is too short");

        byte sw1 = respBytes[^2];
        byte sw2 = respBytes[^1];
        var data = respBytes[..^2];

        // The transport already drains 61 xx / 91 xx with GET RESPONSE. Issuing
        // another one here used to overwrite a good payload with the card's
        // 6D00 answer and turn a successful EnableProfile into a failure.
        // Measured: EnableProfile answers BF31 03 8001 00 followed by 91 0B,
        // where 8001 00 is result code "success"; the switch had in fact run.

        // 6A81 / 6881 after a command means the channel died under us, which is
        // exactly what a SIM REFRESH looks like. Report it as a reset, not as a
        // protocol error, so the switch flow can verify instead of failing.
        if ((sw1 == 0x6A || sw1 == 0x68) && sw2 == 0x81)
            throw new EuiccCardResetException(
                $"指令执行后逻辑通道失效 (SW {sw1:X2} {sw2:X2})：eUICC 已进入 SIM REFRESH 重置，结果需通过重新读取卡片状态判定。");

        // 61 xx / 91 xx with a complete payload means the command succeeded and
        // the card is only announcing follow-up data that this reader cannot
        // fetch. Parse what was received instead of failing the whole command.
        if ((sw1 == 0x61 || sw1 == 0x91) && data.Length > 0)
        {
            try
            {
                var (partialTlv, _) = Tlv.Parse(data);
                return partialTlv;
            }
            catch
            {
                // Payload is truncated: the card is mid-reset. Report it as such
                // so the caller verifies state instead of assuming failure.
                throw new EuiccCardResetException(
                    $"eUICC 返回 91 {sw2:X2} 且响应数据不完整（{data.Length} 字节）：卡片已进入 SIM REFRESH，需复位后核验。");
            }
        }

        // 91 xx with no payload: the card started its REFRESH procedure and is
        // waiting for a terminal acknowledgement this reader will never send.
        if (sw1 == 0x91)
            throw new EuiccCardResetException(
                $"eUICC 返回 91 {sw2:X2}：卡片已启动 SIM REFRESH 流程并等待终端确认，需断电复位解除。");

        if (sw1 != 0x90 || sw2 != 0x00)
            throw new InvalidOperationException($"eUICC returned error SW: {sw1:X2} {sw2:X2} (Data len: {data.Length})");

        if (data.Length == 0)
            throw new InvalidOperationException("eUICC response contained empty payload");

        var (resTlv, _) = Tlv.Parse(data);
        return resTlv;
    }

    /// <summary>
    /// Reads every 16-byte EID returned by the currently addressable eUICC
    /// application. Standard cards return exactly one. Some physical eSIM
    /// adapters expose more than one eUICC identity in their response, so a
    /// caller must not silently discard all but the first one.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetEidsAsync(CancellationToken ct = default)
    {
        var eids = new List<string>();

        void Collect(Tlv response)
        {
            foreach (var eidTlv in response.FindAllRecursive(TagEID))
            {
                // ICCIDs also use tag 5A, but an EID is exactly 16 bytes.
                // Keeping this check prevents a profile ICCID nested in a
                // vendor response from being presented as a second chip.
                if (eidTlv.Value.Length != 16) continue;
                var eid = eidTlv.HexValue().ToUpperInvariant();
                if (!eids.Contains(eid, StringComparer.Ordinal)) eids.Add(eid);
            }
        }

        // Query all standard sources. Apart from providing resilient fallback
        // behaviour, this lets diagnostics retain a vendor's additional EID
        // instead of returning at the first successful response.
        try
        {
            var req = new Tlv(0xBF3E);
            req.Children.Add(new Tlv(0x5C, new byte[] { 0x5A }));
            var resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);
            Collect(resp);
        }
        catch { }

        try
        {
            var req1 = new Tlv(TagGetEuiccInfo1);
            var resp1 = await TransmitCommandAsync(req1, ct).ConfigureAwait(false);
            Collect(resp1);
        }
        catch { }

        try
        {
            var req2 = new Tlv(TagGetEuiccInfo2);
            var resp2 = await TransmitCommandAsync(req2, ct).ConfigureAwait(false);
            Collect(resp2);
        }
        catch { }

        if (eids.Count > 0) return eids;
        throw new InvalidOperationException("未找到卡片 EID (卡片可能未返回 5A 标签或使用定制指令)");
    }

    /// <summary>Compatibility helper for callers that can operate only on the current eUICC.</summary>
    public async Task<string> GetEIDAsync(CancellationToken ct = default) =>
        (await GetEidsAsync(ct).ConfigureAwait(false))[0];

    public async Task<List<Profile>> GetProfilesInfoAsync(CancellationToken ct = default)
    {
        Tlv? resp = null;
        try
        {
            // 1. Try standard empty request body BF 2D 00 first (as in VoCat/lpac, universal SGP.22 compatibility)
            var reqEmpty = new Tlv(TagGetProfilesInfo);
            resp = await TransmitCommandAsync(reqEmpty, ct).ConfigureAwait(false);
        }
        catch
        {
            // 2. Fallback to TagList = 5A (iccid), 4F (isdpAid), 9F70 (state), 90 (nickname), 91 (spn), 92 (name), 93 (iconType), 95 (class)
            var tagList = new byte[] { 0x5A, 0x4F, 0x9F, 0x70, 0x90, 0x91, 0x92, 0x93, 0x95 };
            var req = new Tlv(TagGetProfilesInfo);
            req.Children.Add(new Tlv(0x5C, tagList));
            resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);
        }
        var profiles = new List<Profile>();

        var profileEntries = resp.FindAllRecursive(TagProfileInfo);
        foreach (var entry in profileEntries)
        {
            var p = new Profile();

            // A normal SGP.22 response represents one eUICC and has no EID in
            // E3. Some dual-eUICC physical adapters include an EID here; retain
            // it instead of flattening all profiles into an unlabelled list.
            p.Eid = entry.FindAllRecursive(TagEID)
                .FirstOrDefault(tlv => tlv.Value.Length == 16)
                ?.HexValue()
                .ToUpperInvariant();

            // ICCID (5A)
            // EID and ICCID both use 5A in different response contexts. In a
            // vendor dual-EID profile entry, prefer the non-16-byte value so
            // the EID can never be mistaken for a 32-digit ICCID.
            var iccidTlv = entry.FindAllRecursive(TagICCID)
                .FirstOrDefault(tlv => tlv.Value.Length > 0 && tlv.Value.Length != 16);
            if (iccidTlv != null)
            {
                p.ICCID = Tlv.BcdToIccid(iccidTlv.Value);
            }

            // ISD-P AID (4F)
            if (entry.FindFirstRecursive(TagISDPAID) is { } aidTlv && aidTlv.Value.Length > 0)
            {
                p.ISDPAID = aidTlv.HexValue();
            }

            // Profile State (9F70)
            if (entry.FindFirstRecursive(TagProfileState) is { } stateTlv)
            {
                p.State = (ProfileState)stateTlv.IntValue();
            }

            // Nickname (90)
            if (entry.FindFirstRecursive(TagProfileNickname) is { } nickTlv)
            {
                p.Nickname = nickTlv.TextValue();
            }

            // Service Provider Name (91)
            if (entry.FindFirstRecursive(TagServiceProviderName) is { } spnTlv)
            {
                p.ServiceProviderName = spnTlv.TextValue();
            }

            // Profile Name (92)
            if (entry.FindFirstRecursive(TagProfileName) is { } nameTlv)
            {
                p.ProfileName = nameTlv.TextValue();
            }

            // Icon Type (93)
            if (entry.FindFirstRecursive(TagIconType) is { } iconTlv)
            {
                p.IconType = iconTlv.IntValue();
            }

            // Profile Class (95)
            if (entry.FindFirstRecursive(TagProfileClass) is { } classTlv)
            {
                p.ProfileClass = (ProfileClass)classTlv.IntValue();
            }

            if (!string.IsNullOrEmpty(p.ICCID) || !string.IsNullOrEmpty(p.ISDPAID))
            {
                profiles.Add(p);
            }
        }

        return profiles;
    }

    public async Task EnableProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default)
    {
        var identTlv = BuildProfileIdentifierTlv(iccidOrAid);
        var req = new Tlv(TagEnableProfile);
        req.Children.Add(identTlv);
        req.Children.Add(new Tlv(0x81, [refresh ? (byte)0xFF : (byte)0x00]));

        try
        {
            var resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);
            CheckResultCode(resp, "EnableProfile");
        }
        catch (EuiccCardResetException)
        {
            // EnableProfile with refresh=true makes the card reset. The command
            // was accepted; whether the profile is active is decided by the
            // caller reading the profile list afterwards, not by this response.
            return;
        }
        catch (Exception ex) when (IsCardGoneMessage(ex.Message))
        {
            // Same situation reported as a plain transport error by older paths.
            return;
        }
    }

    public async Task DisableProfileAsync(string iccidOrAid, bool refresh = true, CancellationToken ct = default)
    {
        var identTlv = BuildProfileIdentifierTlv(iccidOrAid);
        var req = new Tlv(TagDisableProfile);
        req.Children.Add(identTlv);
        req.Children.Add(new Tlv(0x81, [refresh ? (byte)0xFF : (byte)0x00]));

        try
        {
            var resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);
            CheckResultCode(resp, "DisableProfile");
        }
        catch (EuiccCardResetException)
        {
            return;
        }
        catch (Exception ex) when (IsCardGoneMessage(ex.Message))
        {
            return;
        }
    }

    public async Task DeleteProfileAsync(string iccidOrAid, CancellationToken ct = default)
    {
        var identTlv = BuildProfileIdentifierTlv(iccidOrAid);
        var req = new Tlv(TagDeleteProfile);
        req.Children.Add(identTlv);

        var resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);
        CheckResultCode(resp, "DeleteProfile");
    }

    public async Task SetNicknameAsync(string iccidOrAid, string nickname, CancellationToken ct = default)
    {
        var identTlv = BuildProfileIdentifierTlv(iccidOrAid);
        var req = new Tlv(TagSetNickname);
        req.Children.Add(identTlv);
        req.Children.Add(new Tlv(TagProfileNickname, nickname));

        var resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);
        CheckResultCode(resp, "SetNickname");
    }

    public async Task<EuiccInfo> GetEuiccInfoAsync(CancellationToken ct = default)
    {
        var req = new Tlv(TagGetEuiccInfo1);
        var resp = await TransmitCommandAsync(req, ct).ConfigureAwait(false);

        var info = new EuiccInfo();
        if (resp.FindFirstRecursive(TagEID) is { } eidTlv)
            info.EID = eidTlv.HexValue();

        if (resp.FindFirstRecursive(0x82) is { } verTlv)
            info.FirmwareVer = verTlv.TextValue();

        if (resp.FindFirstRecursive(0x83) is { } nvramTlv)
            info.FreeNvramBytes = nvramTlv.IntValue();

        return info;
    }

    private static Tlv BuildProfileIdentifierTlv(string iccidOrAid)
    {
        var clean = iccidOrAid.Trim().Replace(" ", "").Replace("-", "");
        Tlv inner;

        // If numeric 16..20 chars -> ICCID BCD
        if (Regex.IsMatch(clean, @"^\d{16,20}$"))
        {
            inner = new Tlv(TagICCID, Tlv.IccidToBcd(clean));
        }
        else if (Regex.IsMatch(clean, @"^[0-9A-Fa-f]{10,32}$"))
        {
            inner = new Tlv(TagISDPAID, HexUtils.FromHexString(clean));
        }
        else
        {
            inner = new Tlv(TagICCID, Tlv.IccidToBcd(clean));
        }

        // GSMA SGP.22 ES10b: profileIdentifier is defined as [0] CHOICE (Tag 0xA0)
        var a0 = new Tlv(0xA0);
        a0.Children.Add(inner);
        return a0;
    }

    /// <summary>
    /// Recognises transport-level messages that mean "the card went away",
    /// which after a refresh-carrying command is expected rather than fatal.
    /// </summary>
    private static bool IsCardGoneMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("channel", StringComparison.OrdinalIgnoreCase)
            || message.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("6A81", StringComparison.OrdinalIgnoreCase)
            || message.Contains("6881", StringComparison.OrdinalIgnoreCase)
            || message.Contains("SIM REFRESH", StringComparison.OrdinalIgnoreCase)
            || message.Contains("无响应", StringComparison.Ordinal)
            || message.Contains("失去响应", StringComparison.Ordinal);
    }

    private static void CheckResultCode(Tlv resp, string opName)
    {
        var resCodeTlv = resp.FindFirstRecursive(TagResultCode);
        if (resCodeTlv != null && resCodeTlv.Value.Length > 0)
        {
            int code = resCodeTlv.IntValue();
            if (code == 0) return;
            // In SGP.22 EnableProfile, code 2 means profileNotInDisabledState (already enabled)
            if (opName == "EnableProfile" && code == 2)
                return;
            throw new InvalidOperationException($"eUICC {opName} operation returned error code: {code} (0x{code:X2})");
        }
    }
}

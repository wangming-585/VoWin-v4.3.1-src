using System.Text.RegularExpressions;
using VoSharp.Sim;

namespace VoWin.Helpers;

/// <summary>
/// Resolves the label shown for a SIM/eSIM profile.
///
/// Priority (highest first):
///   1. User-supplied nickname  — kept in SimPreferences.CardNickname.
///   2. Auto display name         — AT+CNUM (EF-MSISDN) → IMS P-Associated-URI → carrier + ICCID tail.
///   3. Live operator name        — SPN / registered PLMN.
///   4. PLMN fallback             — "PLMN mcc-mnc".
///
/// The auto name is persisted per ICCID (SimPreferences.DisplayName) so the same
/// card keeps the same label after a profile switch or an application restart.
/// </summary>
public static class CardDisplayNameResolver
{
    private static readonly Regex AssociatedUriNumber = new(
        @"(?:tel:|sip:)(?<number>\+?[0-9]{5,15})(?:@|[;>,]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts the subscriber number advertised by IMS in the
    /// P-Associated-URI header, e.g. "tel:+8613800138000;user=phone".
    /// </summary>
    public static string? ExtractNumberFromAssociatedUri(string? associatedUri)
    {
        if (string.IsNullOrWhiteSpace(associatedUri)) return null;
        var match = AssociatedUriNumber.Match(associatedUri);
        return match.Success ? match.Groups["number"].Value : null;
    }

    /// <summary>
    /// Best-effort subscriber number for a card: the MSISDN stored on the card
    /// first, then the number IMS handed out during registration.
    /// </summary>
    public static string? ResolvePhoneNumber(SimIdentity? sim, string? associatedUri = null)
    {
        if (!string.IsNullOrWhiteSpace(sim?.PhoneNumber))
            return sim.PhoneNumber!.Trim();

        return ExtractNumberFromAssociatedUri(associatedUri);
    }

    /// <summary>
    /// Builds the automatic display name for a card.
    /// </summary>
    /// <param name="sim">Live SIM identity (may be null before the card is read).</param>
    /// <param name="associatedUri">IMS P-Associated-URI, used when the card has no MSISDN.</param>
    /// <param name="iccid">ICCID, used for the trailing-digit fallback.</param>
    /// <param name="operatorName">Operator label override (profile SPN for eSIM cards).</param>
    public static string? ResolveAutoDisplayName(
        SimIdentity? sim,
        string? associatedUri,
        string? iccid,
        string? operatorName = null)
    {
        _ = associatedUri; // 号码已独立存储（ICCID 规则列）；自动显示名不再复用号码。
        var label = CarrierDisplayHelper.BuildCardLabel(sim, iccid, operatorName ?? sim?.OperatorName);
        return string.IsNullOrWhiteSpace(label) ? null : label;
    }

    /// <summary>Last four digits of an ICCID, used as a stable fallback label.</summary>
    public static string IccidTail(string? iccid)
    {
        if (string.IsNullOrWhiteSpace(iccid)) return string.Empty;
        var clean = iccid.Trim();
        return clean.Length <= 4 ? clean : clean[^4..];
    }

    /// <summary>
    /// Effective label of a card: the user nickname wins, then the persisted
    /// auto name, then the live operator name.
    /// </summary>
    public static string? ResolveEffectiveName(
        string? userNickname,
        string? autoDisplayName,
        SimIdentity? sim,
        string? associatedUri = null)
    {
        if (!string.IsNullOrWhiteSpace(userNickname)) return userNickname!.Trim();
        if (!string.IsNullOrWhiteSpace(autoDisplayName)
            && !CarrierDisplayHelper.IsBogusPlmnLabel(autoDisplayName)) return autoDisplayName!.Trim();
        if (!string.IsNullOrWhiteSpace(sim?.PhoneNumber)) return sim!.PhoneNumber!.Trim();
        if (!string.IsNullOrWhiteSpace(sim?.OperatorName)) return sim!.OperatorName!.Trim();
        return ExtractNumberFromAssociatedUri(associatedUri);
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate!.Trim();
        }
        return null;
    }
}

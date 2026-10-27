namespace VoWin.Helpers;

/// <summary>
/// Resolves a PLMN (MCC + MNC) to a readable operator brand using the
/// generated MCC/MNC assignment table. Display-only: the resolved brand
/// never influences network identity or routing.
/// </summary>
internal static class PlmnCarrierTable
{
    /// <summary>
    /// Brand for a PLMN. Tries the exact "mcc-mnc" key first, then a
    /// numeric-normalised form so "460-00" and "46000" resolve alike.
    /// </summary>
    public static string? Resolve(string? mcc, string? mnc)
    {
        if (string.IsNullOrWhiteSpace(mcc) || string.IsNullOrWhiteSpace(mnc)) return null;

        var m = mcc.Trim();
        var n = mnc.Trim();
        if (CardLookupTables.PlmnOperator.TryGetValue($"{m}-{n}", out var brand)) return brand;

        var digits = $"{m}{n}".Replace(" ", string.Empty);
        if (CardLookupTables.PlmnOperator.TryGetValue(digits, out var flat)) return flat;

        // Some tables list the MNC with leading zeros trimmed ("460-0" for 460-00).
        var trimmed = n.TrimStart('0');
        if (trimmed.Length > 0 && trimmed != n &&
            CardLookupTables.PlmnOperator.TryGetValue($"{m}-{trimmed}", out var shortBrand))
            return shortBrand;

        // Conversely a caller may hand us a zero-trimmed MNC; re-pad to two digits.
        if (n.Length == 1 &&
            CardLookupTables.PlmnOperator.TryGetValue($"{m}-{n.PadLeft(2, '0')}", out var paddedBrand))
            return paddedBrand;

        return null;
    }

    /// <summary>
    /// Exact-ICCID brand override (MVNOs hosted on a parent PLMN, e.g. Voxi on
    /// Vodafone 234-15). Display-only.
    /// </summary>
    public static string? ResolveIccidBrand(string? iccid)
    {
        if (string.IsNullOrWhiteSpace(iccid)) return null;
        return CardLookupTables.IccidBrand.TryGetValue(iccid.Trim(), out var brand) ? brand : null;
    }

    /// <summary>Country name for an ICCID issuer prefix, or an empty string.</summary>
    public static string? ResolveIccidCountry(string? iccid)
    {
        if (string.IsNullOrWhiteSpace(iccid)) return null;
        var clean = iccid.Trim();
        if (clean.Length < 4) return null;

        // Longest prefix wins: "89212" must beat "8921".
        for (var length = Math.Min(5, clean.Length); length >= 4; length--)
        {
            var prefix = clean[..length];
            if (CardLookupTables.IccidPrefixCountry.TryGetValue(prefix, out var country))
                return country;
        }

        return null;
    }
}

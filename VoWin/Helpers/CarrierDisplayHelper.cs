using VoSharp.Sim;

namespace VoWin.Helpers;

/// <summary>
/// Chooses user-facing labels from live SIM metadata. The bundled lookup
/// tables are display-only: they never influence network identity or routing.
/// </summary>
public static class CarrierDisplayHelper
{
    public static string GetOperatorDisplay(SimIdentity? sim)
    {
        // 归属运营商优先采用卡面真实、可读且非乱码的 SPN 品牌（如 Lebara/Voxi），
        // 其次精确 ICCID 覆盖（区分 MVNO 与宿主 PLMN，如 Voxi vs Vodafone 234-15），
        // 最后才回退 PLMN 品牌表，避免多品牌合并导致识别错误。
        var spn = sim?.OperatorName;
        if (!string.IsNullOrWhiteSpace(spn) && IsPlausibleOperatorLabel(spn))
            return spn!.Trim();

        var iccidBrand = PlmnCarrierTable.ResolveIccidBrand(sim?.Iccid);
        if (!string.IsNullOrWhiteSpace(iccidBrand)) return iccidBrand!;

        var brand = PlmnCarrierTable.Resolve(sim?.Mcc, sim?.Mnc);
        if (!string.IsNullOrWhiteSpace(brand)) return brand!;

        return !string.IsNullOrWhiteSpace(sim?.Mcc)
            ? $"PLMN: {sim.Mcc}-{sim.Mnc}"
            : "未知运营商";
    }

    public static string GetCountryDisplay(SimIdentity? sim)
    {
        var imsiCountry = MccCountryHelper.FindByMcc(sim?.Mcc);
        if (imsiCountry != null)
            return $"{imsiCountry.Flag} {imsiCountry.Name} ({imsiCountry.Code})";

        return !string.IsNullOrWhiteSpace(sim?.Mcc) ? $"MCC: {sim.Mcc}" : "--";
    }

    /// <summary>
    /// Returns the operator text reported by the modem. ICCID itself identifies
    /// a profile, but cannot reliably establish its retail country without a
    /// carrier database or user-supplied profile metadata.
    /// </summary>
    public static string GetCardProfileDisplay(SimIdentity? sim)
    {
        return sim == null ? "未读取 SIM" : GetOperatorDisplay(sim);
    }

    /// <summary>Actual IMSI home PLMN used for network authentication.</summary>
    public static string GetImsiHomeDisplay(SimIdentity? sim)
    {
        var country = MccCountryHelper.FindByMcc(sim?.Mcc);
        var plmn = !string.IsNullOrWhiteSpace(sim?.Mcc) ? $"{sim.Mcc}-{sim.Mnc}" : null;

        if (country != null)
            return string.IsNullOrWhiteSpace(plmn)
                ? $"{country.Flag} {country.Name}"
                : $"{country.Flag} {country.Name} · PLMN {plmn}";

        return plmn ?? "--";
    }

    /// <summary>
    /// Country name for an IMSI home PLMN, without the flag/code decoration.
    /// Returns an empty string when the MCC is not in the built-in table.
    /// </summary>
    public static string GetCountryName(SimIdentity? sim)
    {
        var country = MccCountryHelper.FindByMcc(sim?.Mcc);
        return country?.Name ?? string.Empty;
    }

    /// <summary>
    /// One-line label naming both country and operator, e.g. "英国 EE" or
    /// "中国香港 csl". The country comes from the IMSI home MCC; the operator
    /// comes from the card's SPN / registered PLMN and falls back to the
    /// built-in PLMN table.
    /// </summary>
    public static string GetCountryOperatorDisplay(SimIdentity? sim, string? operatorNameOverride = null)
    {
        var country = MccCountryHelper.FindByMcc(sim?.Mcc);
        var operatorName = FirstNonEmpty(operatorNameOverride, sim?.OperatorName);

        // The live operator name is often just "PLMN 234-15" for PC/SC readers;
        // the built-in table gives a readable brand for those.
        if (IsGenericPlmnLabel(operatorName))
            operatorName = PlmnCarrierTable.ResolveIccidBrand(sim?.Iccid)
                ?? PlmnCarrierTable.Resolve(sim?.Mcc, sim?.Mnc);

        if (country == null && string.IsNullOrWhiteSpace(operatorName))
            return "--";

        var parts = new List<string>();
        if (country != null) parts.Add($"{country.Flag} {country.Name}".Trim());
        if (!string.IsNullOrWhiteSpace(operatorName)) parts.Add(operatorName!);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Operator brand for a PLMN, preferring an explicit profile SPN and
    /// falling back to the built-in table. Returns an empty string when the
    /// operator cannot be named.
    /// </summary>
    public static string GetOperatorBrand(string? mcc, string? mnc, string? operatorNameOverride = null, string? iccid = null)
    {
        var operatorName = FirstNonEmpty(operatorNameOverride);
        if (!IsGenericPlmnLabel(operatorName) && IsPlausibleOperatorLabel(operatorName)) return operatorName!;
        // 卡面 SPN 不可用时，精确 ICCID 覆盖优先于 PLMN 表（区分 MVNO：如 Voxi vs Vodafone 234-15）。
        var iccidBrand = PlmnCarrierTable.ResolveIccidBrand(iccid);
        if (!string.IsNullOrWhiteSpace(iccidBrand)) return iccidBrand!;
        return PlmnCarrierTable.Resolve(mcc, mnc) ?? string.Empty;
    }

    /// <summary>
    /// Canonical auto label for a card: brand (from the PLMN table) + ICCID
    /// tail, falling back to country + tail, then PLMN + tail, then plain tail.
    /// Format example: "Vodafone ···2888".
    /// </summary>
    public static string BuildCardLabel(SimIdentity? sim, string? iccid, string? operatorNameOverride = null)
    {
        var tail = CardDisplayNameResolver.IccidTail(iccid);
        var brand = GetOperatorBrand(sim?.Mcc, sim?.Mnc, operatorNameOverride ?? sim?.OperatorName, iccid);
        if (!string.IsNullOrWhiteSpace(brand))
            return WithTail(brand!, tail);

        var country = MccCountryHelper.FindByMcc(sim?.Mcc);
        if (country != null && !string.IsNullOrWhiteSpace(country.Name))
            return WithTail(country.Name, tail);

        if (!string.IsNullOrWhiteSpace(sim?.Mcc))
            return WithTail($"PLMN {sim.Mcc}-{sim.Mnc}", tail);

        return string.IsNullOrWhiteSpace(tail) ? "未命名 SIM" : $"ICCID ···{tail}";
    }

    private static string WithTail(string head, string tail)
        => string.IsNullOrWhiteSpace(tail) ? head : $"{head} ···{tail}";


    /// <summary>
    /// Heuristic for a believable operator brand / card label. Rejects
    /// repetitive low-information garbage (e.g. "àààà…¥ ···4314") that was
    /// persisted from a bad SPN decode, so the UI falls back to the brand
    /// table or country instead of printing mojibake.
    /// </summary>
    public static bool IsPlausibleOperatorLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Length > 40) return false;
        // skip digits/separator tail like " ···4314"
        var core = System.Text.RegularExpressions.Regex.Replace(t, @"(\s*·{1,4}\s*[0-9]+\s*$|^[0-9]+\s*·{1,4}\s*|\s+)", "");
        if (core.Length == 0) return true; // only tail => acceptable fallback marker
        var asciiLetters = core.Count(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
        // Mojibake from bad SPN decodes concentrates in Latin-1 Supplement
        // (U+00A0–U+00FF, e.g. àèéêäöüçñ¥, "àààà…¥"). Real multi-byte
        // brands (Chinese / Japanese / Korean, e.g. "中国移动") live above
        // U+0100 and must NOT be rejected here.
        var latin1Suspicious = core.Count(c => c >= 0x00A0 && c <= 0x00FF);
        if (latin1Suspicious > 0.5 * core.Length && asciiLetters == 0) return false;
        // 乱码特征 2：同一字符连续重复 4 次及以上（如 "àáááá…"），
        // 以及单个字符占比过高（如卡片DΦQztàááá…" 中重音符占主导）。真实品牌不会如此。
        if (System.Text.RegularExpressions.Regex.IsMatch(core, @"(.)\1{3,}")) return false;
        var maxFreq = core.GroupBy(c => c).Max(g => g.Count());
        if (core.Length >= 5 && maxFreq > 0.4 * core.Length) return false;

        var distinct = core.Distinct().Count();
        if (core.Length >= 4 && distinct <= Math.Max(2, core.Length / 4)) return false;
        return true;
    }

    /// <summary>True when a "PLMN mcc-mnc" label contains an unassigned MCC (dirty persisted data).</summary>
    public static bool IsBogusPlmnLabel(string? operatorName)
    {
        if (string.IsNullOrWhiteSpace(operatorName)) return false;
        var text = operatorName.Trim();
        if (!text.StartsWith("PLMN", StringComparison.OrdinalIgnoreCase)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"PLMN\s*(\d{3})\s*-\s*(\d+)");
        return m.Success && !SimIdentity.IsAssignedMcc(m.Groups[1].Value);
    }

    /// <summary>True when the operator string carries no brand, only raw PLMN digits.</summary>
    public static bool IsGenericPlmnLabel(string? operatorName)
    {
        if (string.IsNullOrWhiteSpace(operatorName)) return true;
        var text = operatorName.Trim();
        return text.StartsWith("PLMN", StringComparison.OrdinalIgnoreCase)
            || (text.Contains('-') && text.All(c => char.IsDigit(c) || c == '-' || c == ' '))
            || (text.Length is 5 or 6 && text.All(char.IsDigit));
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

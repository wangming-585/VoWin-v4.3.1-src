﻿using VoSharp.Euicc.Models;

namespace VoSharp.Euicc.Profiles;

/// <summary>
/// Resolves a human-supplied selector into exactly one installed profile.
/// Resolution is pure: it never touches the card, so the caller can show the
/// operator what will happen before any ES10 mutation is sent.
/// </summary>
public static class ProfileSelector
{
    /// <summary>Minimum digits accepted for a trailing-ICCID match.</summary>
    private const int MinIccidSuffixDigits = 6;

    /// <summary>
    /// Resolves <paramref name="selector"/> against <paramref name="profiles"/>.
    /// Modes are tried in order of specificity, so an exact ICCID always wins over
    /// a nickname that happens to contain the same digits.
    /// </summary>
    /// <param name="selector">
    /// User input: ICCID, list index, ISD-P AID, nickname, profile name, or one of
    /// the keywords "next" / "prev" / "previous" / "active" / "current".
    /// </param>
    /// <param name="profiles">Profiles as reported by ES10 GetProfilesInfo.</param>
    public static ProfileResolution Resolve(string? selector, IReadOnlyList<Profile> profiles)
    {
        if (profiles.Count == 0)
            return ProfileResolution.Fail(ProfileSelectorMode.Iccid, "卡内没有任何已安装的 Profile。");

        var text = (selector ?? string.Empty).Trim();
        if (text.Length == 0)
            return ProfileResolution.Fail(ProfileSelectorMode.Iccid, "未提供 Profile 标识。");

        var keyword = text.ToLowerInvariant();
        switch (keyword)
        {
            case "next":
                return ResolveRelative(profiles, +1, ProfileSelectorMode.Next);
            case "prev":
            case "previous":
                return ResolveRelative(profiles, -1, ProfileSelectorMode.Previous);
            case "active":
            case "current":
                return ResolveActive(profiles);
        }

        // 1) Exact ICCID. The card reports ICCID as digits only, so strip any
        //    formatting the operator pasted in (spaces, dashes, dots). The text must
        //    contain nothing but digits and those separators, otherwise a nickname
        //    such as "voda 2" would be misread as an ICCID.
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        var looksLikeIccid = text.All(c => char.IsAsciiDigit(c) || c is ' ' or '-' or '_' or '.');
        if (looksLikeIccid && digits.Length >= MinIccidSuffixDigits)
        {
            var exact = profiles.FirstOrDefault(p => p.ICCID.Equals(digits, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return ProfileResolution.Ok(exact, ProfileSelectorMode.Iccid);

            // A trailing-suffix match is only safe when it is unique: two
            // profiles sharing a suffix must never be guessed between.
            var suffixMatches = profiles
                .Where(p => p.ICCID.Length >= digits.Length && p.ICCID.EndsWith(digits, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (suffixMatches.Count == 1)
                return ProfileResolution.Ok(suffixMatches[0], ProfileSelectorMode.Iccid);
            if (suffixMatches.Count > 1)
                return ProfileResolution.Fail(ProfileSelectorMode.Iccid,
                    $"ICCID 后缀 {digits} 匹配到 {suffixMatches.Count} 个 Profile，请给出更长的 ICCID。", suffixMatches);
        }

        // 2) ISD-P AID, hex, case-insensitive.
        var isdp = profiles.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.ISDPAID) &&
            p.ISDPAID.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (isdp is not null) return ProfileResolution.Ok(isdp, ProfileSelectorMode.IsdpAid);

        // 3) List index, 1-based, exactly as printed by the profile list.
        if (int.TryParse(text, out var index))
        {
            if (index >= 1 && index <= profiles.Count)
                return ProfileResolution.Ok(profiles[index - 1], ProfileSelectorMode.Index);
            return ProfileResolution.Fail(ProfileSelectorMode.Index,
                $"序号 {index} 超出范围，卡内共有 {profiles.Count} 个 Profile（有效序号 1-{profiles.Count}）。");
        }

        // 4) Nickname, exact then case-insensitive.
        var nickname = profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Nickname) && p.Nickname.Trim() == text)
            ?? profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Nickname) && p.Nickname.Trim().Equals(text, StringComparison.OrdinalIgnoreCase));
        if (nickname is not null) return ProfileResolution.Ok(nickname, ProfileSelectorMode.Nickname);

        // 5) Profile name / service provider name: exact first, then substring.
        //    Substring matching is intentionally the last resort and must be unique.
        var exactName = profiles.FirstOrDefault(p => MatchesName(p, text, exact: true));
        if (exactName is not null) return ProfileResolution.Ok(exactName, ProfileSelectorMode.Name);

        var partial = profiles.Where(p => MatchesName(p, text, exact: false)).ToList();
        if (partial.Count == 1) return ProfileResolution.Ok(partial[0], ProfileSelectorMode.Name);
        if (partial.Count > 1)
            return ProfileResolution.Fail(ProfileSelectorMode.Name,
                $"名称 \"{text}\" 匹配到 {partial.Count} 个 Profile，请改用 ICCID 或序号。", partial);

        return ProfileResolution.Fail(ProfileSelectorMode.Iccid,
            $"无法识别 \"{text}\"：既不是 ICCID、序号、ISD-P AID，也不是任何 Profile 的昵称或名称。");
    }

    /// <summary>Returns the profile that is currently enabled, if any.</summary>
    public static ProfileResolution ResolveActive(IReadOnlyList<Profile> profiles)
    {
        var enabled = profiles.Where(p => p.State == ProfileState.Enabled).ToList();
        return enabled.Count switch
        {
            1 => ProfileResolution.Ok(enabled[0], ProfileSelectorMode.Active),
            0 => ProfileResolution.Fail(ProfileSelectorMode.Active, "当前没有任何 Profile 处于启用状态。"),
            _ => ProfileResolution.Fail(ProfileSelectorMode.Active,
                $"当前有 {enabled.Count} 个 Profile 同时处于启用状态，无法确定哪一个在提供网络服务。", enabled)
        };
    }

    /// <summary>
    /// Walks the list by <paramref name="delta"/> from the enabled profile and wraps
    /// around. With nothing enabled it starts from the first entry, which is the
    /// behaviour an operator expects from "next" on a fresh card.
    /// </summary>
    public static ProfileResolution ResolveRelative(IReadOnlyList<Profile> profiles, int delta, ProfileSelectorMode mode)
    {
        if (profiles.Count == 0)
            return ProfileResolution.Fail(mode, "卡内没有任何已安装的 Profile。");

        var currentIndex = profiles.ToList().FindIndex(p => p.State == ProfileState.Enabled);
        int targetIndex;
        if (currentIndex < 0)
        {
            // Nothing enabled: "next" lands on the first profile, "previous" on the last.
            targetIndex = delta > 0 ? 0 : profiles.Count - 1;
        }
        else
        {
            targetIndex = ((currentIndex + delta) % profiles.Count + profiles.Count) % profiles.Count;
        }

        return ProfileResolution.Ok(profiles[targetIndex], mode);
    }

    private static bool MatchesName(Profile profile, string text, bool exact)
    {
        foreach (var candidate in new[] { profile.Nickname, profile.ProfileName, profile.ServiceProviderName })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var value = candidate.Trim();
            if (exact ? value.Equals(text, StringComparison.OrdinalIgnoreCase)
                      : value.Contains(text, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

﻿using VoSharp.Euicc.Models;
using VoSharp.Euicc.Profiles;
using Xunit;

namespace VoSharp.Tests;

/// <summary>
/// Selector resolution decides which subscription gets activated, so every mode is
/// pinned here. An ambiguous selector must fail rather than pick arbitrarily.
/// </summary>
public class ProfileSelectorTests
{
    private static List<Profile> Card() =>
    [
        new Profile
        {
            ICCID = "89441600001001722687",
            ISDPAID = "A0000005591010FFFFFFFF8900001500",
            ProfileName = "Lebara",
            ServiceProviderName = "Lebara",
            State = ProfileState.Disabled
        },
        new Profile
        {
            ICCID = "8943122603050488938",
            ISDPAID = "A0000005591010FFFFFFFF8900001800",
            ProfileName = "RBM",
            ServiceProviderName = "Red Bull MOBILE/A1",
            Nickname = "redbull",
            State = ProfileState.Disabled
        },
        new Profile
        {
            ICCID = "89441000400316382888",
            ISDPAID = "A0000005591010FFFFFFFF8900001B00",
            ProfileName = "Vodafone UK eSIM",
            ServiceProviderName = "Vodafone UK",
            Nickname = "vodafone+447503895776",
            State = ProfileState.Enabled
        }
    ];

    [Fact]
    public void Resolves_exact_iccid()
    {
        var r = ProfileSelector.Resolve("8943122603050488938", Card());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.Iccid, r.Mode);
        Assert.Equal("RBM", r.Profile!.ProfileName);
    }

    [Fact]
    public void Resolves_iccid_with_pasted_formatting()
    {
        // Operators paste ICCIDs with spaces or dashes; those must still match.
        var r = ProfileSelector.Resolve("8943 1226-0305 0488938", Card());
        Assert.True(r.Success);
        Assert.Equal("8943122603050488938", r.Profile!.ICCID);
    }

    [Fact]
    public void Resolves_unique_iccid_suffix()
    {
        var r = ProfileSelector.Resolve("722687", Card());
        Assert.True(r.Success);
        Assert.Equal("89441600001001722687", r.Profile!.ICCID);
    }

    [Fact]
    public void Resolves_one_based_index()
    {
        var r = ProfileSelector.Resolve("1", Card());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.Index, r.Mode);
        Assert.Equal("Lebara", r.Profile!.ProfileName);

        var last = ProfileSelector.Resolve("3", Card());
        Assert.True(last.Success);
        Assert.Equal("Vodafone UK eSIM", last.Profile!.ProfileName);
    }

    [Fact]
    public void Rejects_out_of_range_index()
    {
        var r = ProfileSelector.Resolve("9", Card());
        Assert.False(r.Success);
        Assert.Contains("超出范围", r.Error);
    }

    [Fact]
    public void Resolves_isdp_aid_case_insensitively()
    {
        var r = ProfileSelector.Resolve("a0000005591010ffffffff8900001800", Card());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.IsdpAid, r.Mode);
        Assert.Equal("RBM", r.Profile!.ProfileName);
    }

    [Fact]
    public void Resolves_nickname()
    {
        var r = ProfileSelector.Resolve("redbull", Card());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.Nickname, r.Mode);
        Assert.Equal("RBM", r.Profile!.ProfileName);
    }

    [Fact]
    public void Resolves_profile_name_exactly()
    {
        var r = ProfileSelector.Resolve("Lebara", Card());
        Assert.True(r.Success);
        Assert.Equal("Lebara", r.Profile!.ProfileName);
    }

    [Fact]
    public void Resolves_name_substring_when_unique()
    {
        var r = ProfileSelector.Resolve("Red Bull", Card());
        Assert.True(r.Success);
        Assert.Equal("RBM", r.Profile!.ProfileName);
    }

    [Fact]
    public void Resolves_active_profile()
    {
        var r = ProfileSelector.Resolve("active", Card());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.Active, r.Mode);
        Assert.Equal("89441000400316382888", r.Profile!.ICCID);
    }

    [Fact]
    public void Active_fails_when_multiple_profiles_enabled()
    {
        var card = Card();
        card[0].State = ProfileState.Enabled;
        var r = ProfileSelector.Resolve("active", card);
        Assert.False(r.Success);
        Assert.Contains("同时处于启用状态", r.Error);
        Assert.Equal(2, r.Candidates.Count);
    }

    [Fact]
    public void Next_wraps_to_first_after_last()
    {
        var card = Card();
        // Index 3 (Vodafone) is enabled; next must wrap to index 1 (Lebara).
        var r = ProfileSelector.Resolve("next", card);
        Assert.True(r.Success);
        Assert.Equal("Lebara", r.Profile!.ProfileName);
    }

    [Fact]
    public void Prev_wraps_to_last_before_first()
    {
        var card = Card();
        card[2].State = ProfileState.Disabled;
        card[0].State = ProfileState.Enabled;
        var r = ProfileSelector.Resolve("prev", card);
        Assert.True(r.Success);
        Assert.Equal("Vodafone UK eSIM", r.Profile!.ProfileName);
    }

    [Fact]
    public void Next_from_nothing_enabled_starts_at_first()
    {
        var card = Card();
        foreach (var p in card) p.State = ProfileState.Disabled;
        var r = ProfileSelector.Resolve("next", card);
        Assert.True(r.Success);
        Assert.Equal("Lebara", r.Profile!.ProfileName);
    }

    [Fact]
    public void Unknown_selector_reports_all_available_profiles_in_error()
    {
        var r = ProfileSelector.Resolve("no-such-thing", Card());
        Assert.False(r.Success);
        Assert.Contains("无法识别", r.Error);
    }

    [Fact]
    public void Empty_card_is_rejected()
    {
        var r = ProfileSelector.Resolve("1", []);
        Assert.False(r.Success);
        Assert.Contains("没有任何已安装", r.Error);
    }

    [Fact]
    public void Progress_renders_step_counter_and_failure_flag()
    {
        var normal = new ProfileSwitchProgress(45, "发送 EnableProfile 指令", "第 1/3 次尝试", 4, 9);
        Assert.Equal("[4/9]  45% 发送 EnableProfile 指令（第 1/3 次尝试）", normal.ToDisplayString());

        var failed = new ProfileSwitchProgress(100, "切卡失败", "3 次尝试均未生效", 9, 9, Failed: true);
        Assert.Contains("失败", failed.ToDisplayString());
        Assert.StartsWith("[9/9]", failed.ToDisplayString());
    }
}

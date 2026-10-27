namespace VoSharp.Euicc.Models;

public enum ProfileState
{
    Disabled = 0,
    Enabled = 1
}

public enum ProfileClass
{
    Test = 0,
    Provisioning = 1,
    Operational = 2
}

public class Profile
{
    /// <summary>ISD-R application AID that owns this profile. Required on multi-eUICC cards.</summary>
    public string? EuiccAid { get; set; }

    /// <summary>
    /// Owning eUICC identity when the card exposes it alongside profile data.
    /// Standard single-eUICC responses omit this; dual-EID adapters may include
    /// a 16-byte 5A value in each profile container.
    /// </summary>
    public string? Eid { get; set; }

    /// <summary>
    /// Only profiles on the currently addressed eUICC may receive ES10
    /// mutations. The UI uses this to prevent a dual-EID card from appearing
    /// to switch a profile on an inaccessible chip.
    /// </summary>
    public bool IsCurrentlyAddressable { get; set; } = true;

    public string ICCID { get; set; } = string.Empty;
    public bool IsEnabledState => State == ProfileState.Enabled;
    public string ISDPAID { get; set; } = string.Empty;
    public ProfileState State { get; set; } = ProfileState.Disabled;
    public string? Nickname { get; set; }
    public string? ServiceProviderName { get; set; }
    public string? ProviderName => ServiceProviderName ?? ProfileName;
    public string? ProfileName { get; set; }
    public ProfileClass ProfileClass { get; set; } = ProfileClass.Operational;
    public int IconType { get; set; }

    public override string ToString()
    {
        var name = !string.IsNullOrWhiteSpace(Nickname) ? Nickname : (!string.IsNullOrWhiteSpace(ProfileName) ? ProfileName : "Unnamed");
        var sp = !string.IsNullOrWhiteSpace(ServiceProviderName) ? $" ({ServiceProviderName})" : "";
        var eid = string.IsNullOrWhiteSpace(Eid) ? string.Empty : $" | EID={Eid}";
        return $"[{State}] ICCID={ICCID} | {name}{sp}{eid}";
    }
}

/// <summary>
/// One independently selectable eUICC storage.  EID alone identifies the chip,
/// whereas ES10 operations must select its ISD-R AID before changing profiles.
/// </summary>
public sealed record EuiccInventoryEntry(string Eid, string Aid, IReadOnlyList<Profile> Profiles)
{
    public string DisplayName => $"EID …{(Eid.Length > 4 ? Eid[^4..] : Eid)} · ISD-R …{(Aid.Length > 4 ? Aid[^4..] : Aid)}";
}

public class EuiccInfo
{
    public string EID { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? FirmwareVer { get; set; }
    public int FreeNvramBytes { get; set; }
    public string? DefaultSmdp { get; set; }
    public string? RootDs { get; set; }
}

/// <summary>
/// Progress indicator for profile switching operation, with discrete stages
/// mapped to 0-100 percent completion.
/// </summary>
/// <param name="Percent">Progress percentage (0-100).</param>
/// <param name="Stage">Human-readable stage description (Chinese).</param>
/// <param name="Detail">Optional additional detail (e.g., retry count).</param>
/// <param name="Step">1-based index of the current step; 0 when not step-based.</param>
/// <param name="TotalSteps">Total number of steps in the plan; 0 when unknown.</param>
/// <param name="Failed">True when this report marks a failure the operator must see.</param>
public sealed record ProfileSwitchProgress(
    int Percent,
    string Stage,
    string? Detail = null,
    int Step = 0,
    int TotalSteps = 0,
    bool Failed = false)
{
    /// <summary>
    /// Single-line rendering for consoles and logs: "[3/8] 40% 正在发送 Enable 指令".
    /// A failed report is prefixed so a failure can never be mistaken for progress.
    /// </summary>
    public string ToDisplayString()
    {
        var counter = TotalSteps > 0 ? $"[{Step}/{TotalSteps}] " : string.Empty;
        var flag = Failed ? "失败 " : string.Empty;
        var detail = string.IsNullOrWhiteSpace(Detail) ? string.Empty : $"（{Detail}）";
        return $"{counter}{Percent,3}% {flag}{Stage}{detail}";
    }
}

/// <summary>
/// How a caller names the profile to activate. Every mode resolves to exactly one
/// installed profile before any ES10 mutation is sent, so a typo can never enable
/// an unintended subscription.
/// </summary>
/// <summary>
/// Dispatches progress reports synchronously on the calling thread.
/// <see cref="Progress{T}"/> posts to the captured context or, when there is
/// none (console, background service), to the thread pool. That reordering made
/// "[4/8]" print after "[5/8]" and let a completion message appear before the
/// final step, which is exactly the confusion the step counter exists to avoid.
/// </summary>
public sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public SynchronousProgress(Action<T> handler) => _handler = handler;

    public void Report(T value) => _handler(value);
}

public enum ProfileSelectorMode
{
    /// <summary>Full ICCID (or its trailing digits, at least 6).</summary>
    Iccid = 0,
    /// <summary>1-based ordinal in the card's profile list.</summary>
    Index = 1,
    /// <summary>ISD-P AID of the profile.</summary>
    IsdpAid = 2,
    /// <summary>Nickname set via ES10 SetNickname.</summary>
    Nickname = 3,
    /// <summary>Profile name / service provider name, exact then substring.</summary>
    Name = 4,
    /// <summary>The next profile in list order after the currently enabled one.</summary>
    Next = 5,
    /// <summary>The previous profile in list order after the currently enabled one.</summary>
    Previous = 6,
    /// <summary>Alias for the profile that is currently enabled.</summary>
    Active = 7
}

/// <summary>Outcome of resolving a selector string against the installed profiles.</summary>
/// <param name="Profile">The single resolved profile, or null when resolution failed.</param>
/// <param name="Mode">Selector interpretation that produced the match.</param>
/// <param name="Error">Chinese explanation when resolution failed; null on success.</param>
/// <param name="Candidates">All profiles that matched, to explain an ambiguous selector.</param>
public sealed record ProfileResolution(
    Profile? Profile,
    ProfileSelectorMode Mode,
    string? Error,
    IReadOnlyList<Profile> Candidates)
{
    public bool Success => Profile is not null && Error is null;

    public static ProfileResolution Ok(Profile profile, ProfileSelectorMode mode)
        => new(profile, mode, null, [profile]);

    public static ProfileResolution Fail(ProfileSelectorMode mode, string error, IReadOnlyList<Profile>? candidates = null)
        => new(null, mode, error, candidates ?? []);
}

/// <summary>
/// Strategy used to bring a profile online. Cards differ in how strictly they
/// enforce the "one enabled profile" rule, so the caller can pick the approach.
/// </summary>
public enum ProfileSwitchMode
{
    /// <summary>
    /// Enable the target only. Fastest, and correct for cards that accept a
    /// second enabled profile or where nothing else is enabled.
    /// </summary>
    EnableOnly = 0,
    /// <summary>
    /// Enable the target, and only if that fails, disable every other enabled
    /// profile and retry. Default: safe on both permissive and strict cards.
    /// </summary>
    EnableWithFallback = 1,
    /// <summary>
    /// Always disable other enabled profiles first, then enable the target.
    /// Required by cards that reject EnableProfile while another profile is on.
    /// </summary>
    DisableOthersFirst = 2
}

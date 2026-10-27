using System.IO;
using VoSharp.Euicc.Asn1;
using VoSharp.Euicc.Models;
using VoSharp.Euicc.Profiles;
using VoSharp.Euicc.Download;

namespace VoWin.Tests;

// ==================== TLV ====================
public class TlvTests
{
    private static Tlv Prim(uint tag, params byte[] value) => new(tag, value);
    private static Tlv Cons(uint tag, params Tlv[] children)
    {
        var t = new Tlv(tag);
        foreach (var c in children) t.Children.Add(c);
        return t;
    }

    [Fact]
    public void Encode_ShortFormLength_EncodesCorrectly()
    {
        byte[] encoded = Prim(0x01, 0xAB, 0xCD).Encode();
        Assert.Equal(new byte[] { 0x01, 0x02, 0xAB, 0xCD }, encoded);
    }

    [Fact]
    public void Parse_RoundTrip_SinglePrimitive()
    {
        byte[] encoded = Prim(0x02, 0xDE, 0xAD, 0xBE, 0xEF).Encode();
        var (tlv, consumed) = Tlv.Parse(encoded);
        Assert.Equal(0x02u, tlv.Tag);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, tlv.Value);
        Assert.Equal(encoded.Length, consumed);
    }

    [Fact]
    public void Parse_RoundTrip_ConstructedWithChildren()
    {
        var node = Cons(0xA0, Prim(0x01, 0xAA), Prim(0x02, 0xBB, 0xCC));
        byte[] encoded = node.Encode();
        var (tlv, consumed) = Tlv.Parse(encoded);
        Assert.Equal(0xA0u, tlv.Tag);
        Assert.Equal(2, tlv.Children.Count);
        Assert.Equal(0x01u, tlv.Children[0].Tag);
        Assert.Equal(0xAA, tlv.Children[0].Value[0]);
        Assert.Equal(0x02u, tlv.Children[1].Tag);
        Assert.Equal(new byte[] { 0xBB, 0xCC }, tlv.Children[1].Value);
        Assert.Equal(encoded.Length, consumed);
    }

    [Fact]
    public void Encode_Constructed_PreservesChildOrder()
    {
        var node = Cons(0xA1, Prim(0x01, 0x10), Prim(0x02, 0x20), Prim(0x03, 0x30));
        byte[] encoded = node.Encode();
        var (tlv, _) = Tlv.Parse(encoded);
        Assert.Equal(new uint[] { 0x01, 0x02, 0x03 }, tlv.Children.Select(c => c.Tag).ToArray());
    }

    [Fact]
    public void Parse_LongFormLength_ThreeBytes()
    {
        // tag 0x01, length 0x82 0x01 0x00 (256), value 256 bytes of 0x5A
        var payload = new byte[256];
        Array.Fill(payload, (byte)0x5A);
        var data = new List<byte>() { 0x01, 0x82, 0x01, 0x00 };
        data.AddRange(payload);
        var (tlv, consumed) = Tlv.Parse(data.ToArray());
        Assert.Equal(256, tlv.Value.Length);
        Assert.Equal(0x5A, tlv.Value[255]);
        Assert.Equal(data.Count, consumed);
    }

    [Fact]
    public void Parse_TruncatedValue_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Tlv.Parse(new byte[] { 0x01, 0x05, 0xAA }));
    }

    [Fact]
    public void Parse_EmptyData_Throws()
    {
        Assert.Throws<ArgumentException>(() => Tlv.Parse(Array.Empty<byte>()));
    }

    [Fact]
    public void Parse_InvalidLengthEncoding_Throws()
    {
        // 0x80 means "number of length bytes = 0", which is invalid
        Assert.Throws<InvalidOperationException>(() => Tlv.Parse(new byte[] { 0x01, 0x80 }));
    }

    [Fact]
    public void Parse_LengthTooLarge_Throws()
    {
        // length encoding numBytes=4, value 0x7FFFFFFF > 10MB
        Assert.Throws<InvalidOperationException>(() => Tlv.Parse(new byte[] { 0x01, 0x84, 0x7F, 0xFF, 0xFF, 0xFF }));
    }

    [Fact]
    public void Parse_DepthLimitExceeded_Throws()
    {
        // Build a chain of constructed nodes deeper than MaxTlvDepth (32)
        Tlv node = Prim(0x01, 0x00);
        for (int i = 0; i < Tlv.MaxTlvDepth + 3; i++)
            node = Cons(0xA0, node);
        var encoded = node.Encode();
        Assert.Throws<InvalidOperationException>(() => Tlv.Parse(encoded));
    }

    [Fact]
    public void Parse_NodeCountExceeded_Throws()
    {
        var parent = new Tlv(0xA0);
        for (int i = 0; i < Tlv.MaxTlvNodes + 10; i++)
            parent.Children.Add(Prim(0x01, 0x00));
        var encoded = parent.Encode();
        Assert.Throws<InvalidOperationException>(() => Tlv.ParseAll(encoded));
    }

    [Fact]
    public void IntValue_BigEndian_Calculates()
    {
        Assert.Equal(0x010203, Prim(0x01, 0x01, 0x02, 0x03).IntValue());
    }

    [Fact]
    public void IntValue_EmptyValue_ReturnsZero()
    {
        Assert.Equal(0, Prim(0x01).IntValue());
    }

    [Fact]
    public void FindAllRecursive_FindsAllMatches()
    {
        var root = Cons(0xA0,
            Cons(0xA0, Prim(0x10, 0x01), Prim(0x11, 0x02)),
            Prim(0x10, 0x03),
            Cons(0xA1, Prim(0x10, 0x04)));
        var matches = root.FindAllRecursive(0x10);
        Assert.Equal(3, matches.Count);
        Assert.Equal(new byte[] { 0x01 }, matches[0].Value);
        Assert.Equal(new byte[] { 0x03 }, matches[1].Value);
        Assert.Equal(new byte[] { 0x04 }, matches[2].Value);
    }

    [Fact]
    public void FindFirstRecursive_PrefersRootThenDepthFirst()
    {
        var node = Cons(0x10, Prim(0x10, 0xAA), Prim(0x10, 0xBB));
        var found = node.FindFirstRecursive(0x10);
        Assert.NotNull(found);
        Assert.Equal(node, found); // root itself matches first
    }

    [Fact]
    public void FindFirstChild_ReturnsDirectChild()
    {
        var node = Cons(0xA0, Prim(0x10, 0xAA), Prim(0x11, 0xBB));
        var child = node.FindFirstChild(0x11);
        Assert.NotNull(child);
        Assert.Equal(0xBB, child.Value[0]);
        Assert.Null(node.FindFirstChild(0x99));
    }

    [Fact]
    public void BcdToIccid_Decodes()
    {
        Assert.Equal("123456", Tlv.BcdToIccid(new byte[] { 0x21, 0x43, 0x65 }));
    }

    [Fact]
    public void IccidToBcd_Encodes()
    {
        Assert.Equal(new byte[] { 0x21, 0x43, 0x65 }, Tlv.IccidToBcd("123456"));
    }

    [Fact]
    public void IccidToBcd_Bcd_RoundTrip_OddLength()
    {
        const string iccid = "8986001234567890123"; // 19 digits, odd
        var bcd = Tlv.IccidToBcd(iccid);
        Assert.Equal(iccid, Tlv.BcdToIccid(bcd));
    }

    [Fact]
    public void IccidToBcd_Bcd_RoundTrip_EvenLength()
    {
        const string iccid = "89860012345678901234";
        var bcd = Tlv.IccidToBcd(iccid);
        Assert.Equal(iccid, Tlv.BcdToIccid(bcd));
    }

    [Fact]
    public void HexValue_TextValue_Helpers()
    {
        var t = Prim(0x01, 0xAB, 0xCD);
        Assert.Equal("ABCD", t.HexValue());
        var txt = new Tlv(0x01, "hi");
        Assert.Equal("hi", txt.TextValue());
    }
}

// ==================== ProfileSelector ====================
public class ProfileSelectorTests
{
    private static Profile P(string iccid, string isdp = "", string? nick = null, string? name = null, string? sp = null, ProfileState st = ProfileState.Disabled)
        => new() { ICCID = iccid, ISDPAID = isdp, Nickname = nick, ProfileName = name, ServiceProviderName = sp, State = st };

    private static IReadOnlyList<Profile> ThreeProfiles() => new List<Profile>
    {
        P("89860012345678901234", "A0000005591010F10000000000000001", "germany", "Vodafone DE", "Vodafone", ProfileState.Enabled),
        P("89860011112222333344", "A0000005591010F10000000000000002", "france", "Orange FR", "Orange", ProfileState.Disabled),
        P("89860055556666777788", "A0000005591010F10000000000000003", null, "T-Mobile DE", "T-Mobile", ProfileState.Disabled),
    };

    [Fact]
    public void Resolve_ExactIccid_Match()
    {
        var r = ProfileSelector.Resolve("89860012345678901234", ThreeProfiles());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.Iccid, r.Mode);
        Assert.Equal("89860012345678901234", r.Profile!.ICCID);
    }

    [Fact]
    public void Resolve_IccidSuffix_UniqueMatch()
    {
        var r = ProfileSelector.Resolve("78901234", ThreeProfiles());
        Assert.True(r.Success);
        Assert.Equal("89860012345678901234", r.Profile!.ICCID);
    }

    [Fact]
    public void Resolve_IccidSuffix_Ambiguous_Fail()
    {
        var profiles = new List<Profile> { P("1118986001234567890"), P("2228986001234567890") };
        // suffix 4567890 (7 digits) matches both -> ambiguous fail
        var r = ProfileSelector.Resolve("4567890", profiles);
        Assert.False(r.Success);
        Assert.Equal(2, r.Candidates.Count);
    }

    [Fact]
    public void Resolve_IsdpAid_Match()
    {
        var r = ProfileSelector.Resolve("A0000005591010F10000000000000002", ThreeProfiles());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.IsdpAid, r.Mode);
        Assert.Equal("89860011112222333344", r.Profile!.ICCID);
    }

    [Fact]
    public void Resolve_Index_Match()
    {
        var r = ProfileSelector.Resolve("2", ThreeProfiles());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.Index, r.Mode);
        Assert.Equal("89860011112222333344", r.Profile!.ICCID);
    }

    [Fact]
    public void Resolve_Index_OutOfRange_Fail()
    {
        var r = ProfileSelector.Resolve("9", ThreeProfiles());
        Assert.False(r.Success);
        Assert.Equal(ProfileSelectorMode.Index, r.Mode);
    }

    [Fact]
    public void Resolve_Nickname_ExactCaseInsensitive()
    {
        var r = ProfileSelector.Resolve("GERMANY", ThreeProfiles());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.Nickname, r.Mode);
    }

    [Fact]
    public void Resolve_ProfileName_Exact()
    {
        var r = ProfileSelector.Resolve("Orange FR", ThreeProfiles());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.Name, r.Mode);
    }

    [Fact]
    public void Resolve_ServiceProvider_Exact()
    {
        var r = ProfileSelector.Resolve("T-Mobile", ThreeProfiles());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.Name, r.Mode);
        Assert.Equal("89860055556666777788", r.Profile!.ICCID);
    }

    [Fact]
    public void Resolve_ProfileName_Substring_Unique()
    {
        var single = ProfileSelector.Resolve("Orange", ThreeProfiles());
        Assert.True(single.Success);
        Assert.Equal(ProfileSelectorMode.Name, single.Mode);
    }

    [Fact]
    public void Resolve_ProfileName_Substring_Ambiguous_Fail()
    {
        var r = ProfileSelector.Resolve("de", ThreeProfiles());
        Assert.False(r.Success);
        Assert.Equal(ProfileSelectorMode.Name, r.Mode);
        Assert.Equal(2, r.Candidates.Count);
    }

    [Fact]
    public void Resolve_Next_EnabledWrapAround()
    {
        var r = ProfileSelector.Resolve("next", ThreeProfiles());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.Next, r.Mode);
        Assert.Equal("89860011112222333344", r.Profile!.ICCID); // after enabled (index 0) -> index 1
    }

    [Fact]
    public void Resolve_Next_NoneEnabled_StartsAtFirst()
    {
        var profiles = new List<Profile> { P("A"), P("B") };
        var r = ProfileSelector.Resolve("next", profiles);
        Assert.True(r.Success);
        Assert.Equal("A", r.Profile!.ICCID);
    }

    [Fact]
    public void Resolve_Prev_NoneEnabled_LandsOnLast()
    {
        var profiles = new List<Profile> { P("A"), P("B"), P("C") };
        var r = ProfileSelector.Resolve("previous", profiles);
        Assert.True(r.Success);
        Assert.Equal("C", r.Profile!.ICCID);
    }

    [Fact]
    public void Resolve_Active_ExactlyOne()
    {
        var r = ProfileSelector.Resolve("active", ThreeProfiles());
        Assert.True(r.Success);
        Assert.Equal(ProfileSelectorMode.Active, r.Mode);
        Assert.Equal("89860012345678901234", r.Profile!.ICCID);
    }

    [Fact]
    public void Resolve_Active_None_Fail()
    {
        var profiles = new List<Profile> { P("A"), P("B") };
        var r = ProfileSelector.Resolve("current", profiles);
        Assert.False(r.Success);
        Assert.Equal(ProfileSelectorMode.Active, r.Mode);
    }

    [Fact]
    public void Resolve_Active_Multiple_Fail()
    {
        var profiles = new List<Profile> { P("A", st: ProfileState.Enabled), P("B", st: ProfileState.Enabled) };
        var r = ProfileSelector.Resolve("active", profiles);
        Assert.False(r.Success);
        Assert.Equal(2, r.Candidates.Count);
    }

    [Fact]
    public void Resolve_EmptyProfiles_Fail()
    {
        var r = ProfileSelector.Resolve("anything", Array.Empty<Profile>());
        Assert.False(r.Success);
    }

    [Fact]
    public void Resolve_EmptySelector_Fail()
    {
        var r = ProfileSelector.Resolve("   ", ThreeProfiles());
        Assert.False(r.Success);
    }

    [Fact]
    public void Resolve_Unrecognized_Fail()
    {
        var r = ProfileSelector.Resolve("%%%unknown%%%", ThreeProfiles());
        Assert.False(r.Success);
    }

    [Fact]
    public void ResolveFormccidWithSeparators_IgnoresFormatting()
    {
        var r = ProfileSelector.Resolve("8986 0012-3456.7890-1234", ThreeProfiles());
        Assert.True(r.Success);
        Assert.Equal("89860012345678901234", r.Profile!.ICCID);
    }
}

// ==================== EuiccDownloadJournal ====================
[CollectionDefinition("EuiccJournal", DisableParallelization = true)]
public sealed class EuiccJournalCollection { }

[Collection("EuiccJournal")]
public class EuiccDownloadJournalTests : IDisposable
{
    private readonly string _tempFile;
    private readonly string? _savedPath;

    public EuiccDownloadJournalTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), "euicc-journal-test-" + Guid.NewGuid().ToString("N") + ".json");
        _savedPath = EuiccDownloadJournal.PathOverride;
        EuiccDownloadJournal.PathOverride = _tempFile;
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    public void Dispose()
    {
        EuiccDownloadJournal.PathOverride = _savedPath;
        try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
    }

    [Fact]
    public void Begin_CreatesInProgressEntry()
    {
        var fp = EuiccDownloadJournal.Fingerprint("LPA:1$SMDP.PROD$ACTIVATION");
        EuiccDownloadJournal.Begin(fp, new[] { "A" });
        var entry = EuiccDownloadJournal.Get(fp);
        Assert.NotNull(entry);
        Assert.Equal("in_progress", entry.State);
    }

    [Fact]
    public void Begin_OffersDistinctNonEmptyProfiles()
    {
        var fp = EuiccDownloadJournal.Fingerprint("code-1");
        EuiccDownloadJournal.Begin(fp, new[] { "", "A", "a", "B", "  " });
        var entry = EuiccDownloadJournal.Get(fp)!;
        Assert.Equal(new[] { "A", "B" }, entry.ProfilesBefore);
    }

    [Fact]
    public void Begin_RepeatedFingerprint_Throws()
    {
        var fp = EuiccDownloadJournal.Fingerprint("code-2");
        EuiccDownloadJournal.Begin(fp, new[] { "A" });
        Assert.Throws<InvalidOperationException>(() => EuiccDownloadJournal.Begin(fp, new[] { "B" }));
    }

    [Fact]
    public void Complete_SetsStateAndIccid()
    {
        var fp = EuiccDownloadJournal.Fingerprint("code-3");
        EuiccDownloadJournal.Begin(fp, new[] { "A" });
        EuiccDownloadJournal.Complete(fp, "89860012345678901234");
        var entry = EuiccDownloadJournal.Get(fp)!;
        Assert.Equal("completed", entry.State);
        Assert.Equal("89860012345678901234", entry.InstalledIccid);
    }

    [Fact]
    public void MarkUncertain_SetsUncertain()
    {
        var fp = EuiccDownloadJournal.Fingerprint("code-4");
        EuiccDownloadJournal.Begin(fp, new[] { "A" });
        EuiccDownloadJournal.MarkUncertain(fp);
        Assert.Equal("uncertain", EuiccDownloadJournal.Get(fp)!.State);
    }

    [Fact]
    public void RemoveIfRetrySafe_RemovesEntry()
    {
        var fp = EuiccDownloadJournal.Fingerprint("code-5");
        EuiccDownloadJournal.Begin(fp, new[] { "A" });
        EuiccDownloadJournal.RemoveIfRetrySafe(fp);
        Assert.Null(EuiccDownloadJournal.Get(fp));
    }

    [Fact]
    public void ResetForAuthorizedRetry_ReturnsTrue_Removes()
    {
        var fp = EuiccDownloadJournal.Fingerprint("code-6");
        EuiccDownloadJournal.Begin(fp, new[] { "A" });
        Assert.True(EuiccDownloadJournal.ResetForAuthorizedRetry(fp));
        Assert.Null(EuiccDownloadJournal.Get(fp));
    }

    [Fact]
    public void ResetForAuthorizedRetry_NotExist_ReturnsFalse()
    {
        var fp = EuiccDownloadJournal.Fingerprint("code-7");
        Assert.False(EuiccDownloadJournal.ResetForAuthorizedRetry(fp));
    }

    [Fact]
    public void Get_UnknownFingerprint_ReturnsNull()
    {
        Assert.Null(EuiccDownloadJournal.Get("deadbeef"));
    }

    [Fact]
    public void Fingerprint_DeterministicSha256()
    {
        var a = EuiccDownloadJournal.Fingerprint("same-input");
        var b = EuiccDownloadJournal.Fingerprint("same-input");
        var c = EuiccDownloadJournal.Fingerprint("different-input");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(64, a.Length); // 32 bytes hex
    }

    [Fact]
    public void Load_CorruptJson_ThrowsInvalidDataException()
    {
        File.WriteAllText(_tempFile, "{ not valid json !!!");
        Assert.Throws<InvalidDataException>(() => EuiccDownloadJournal.Get("anything"));
    }
}

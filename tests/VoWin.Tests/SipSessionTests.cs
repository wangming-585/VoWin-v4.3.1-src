using VoSharp.Sip;

namespace VoWin.Tests;

public class SipRegisterSessionStaticTests
{
    // ==================== DecodeAkaNonce ====================
    [Fact]
    public void DecodeAkaNonce_Base64_RandAutn()
    {
        var rand = Enumerable.Repeat((byte)0x11, 16).ToArray();
        var autn = Enumerable.Repeat((byte)0x22, 16).ToArray();
        var nonce = Convert.ToBase64String(rand.Concat(autn).ToArray());
        var (r, a) = SipRegisterSession.DecodeAkaNonce(nonce);
        Assert.Equal(rand, r);
        Assert.Equal(autn, a);
    }

    [Fact]
    public void DecodeAkaNonce_Hex64Chars_PreferredOverBase64()
    {
        // 64 个十六进制字符：既是合法 hex 又是合法 Base64 长度，必须按 hex 解
        var rand = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var autn = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
        var hex = Convert.ToHexString(rand.Concat(autn).ToArray());
        var (r, a) = SipRegisterSession.DecodeAkaNonce(hex);
        Assert.Equal(rand, r);
        Assert.Equal(autn, a);
    }

    [Fact]
    public void DecodeAkaNonce_HexWithDashes()
    {
        var bytes = Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();
        var hex = Convert.ToHexString(bytes);
        var dashed = hex[..8] + "-" + hex[8..];
        var (r, a) = SipRegisterSession.DecodeAkaNonce(dashed);
        Assert.Equal(bytes[..16], r);
        Assert.Equal(bytes[16..], a);
    }

    [Fact]
    public void DecodeAkaNonce_LongerThan32_TruncatedTo32()
    {
        var payload = new byte[40];
        new Random(42).NextBytes(payload);
        var nonce = Convert.ToBase64String(payload);
        var (r, a) = SipRegisterSession.DecodeAkaNonce(nonce);
        Assert.Equal(16, r.Length);
        Assert.Equal(16, a.Length);
        Assert.Equal(payload[..16], r);
        Assert.Equal(payload[16..32], a);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DecodeAkaNonce_Empty_Throws(string nonce)
        => Assert.Throws<InvalidOperationException>(() => SipRegisterSession.DecodeAkaNonce(nonce));

    [Fact]
    public void DecodeAkaNonce_ShortBase64_Throws()
    {
        // Base64 解码后不足 32 字节
        var nonce = Convert.ToBase64String(new byte[16]);
        var ex = Assert.Throws<InvalidOperationException>(() => SipRegisterSession.DecodeAkaNonce(nonce));
        Assert.Contains("16", ex.Message);
    }

    [Fact]
    public void DecodeAkaNonce_NeitherHexNorBase64_Throws()
    {
        // 含非 Base64 字符（! ? 不可出现在 Base64）
        Assert.Throws<InvalidOperationException>(() => SipRegisterSession.DecodeAkaNonce("abc?!xyz"));
    }
}

// ==================== SipMessage 补充 ====================
public class SipMessageAdvancedTests
{
    [Fact]
    public void SetHeader_Add_And_GetHeader()
    {
        var m = new SipMessage { Method = "REGISTER", RequestUri = "sip:x" };
        m.SetHeader("From", "<sip:a@b>;tag=1");
        Assert.Equal("<sip:a@b>;tag=1", m.GetHeader("From"));
        m.SetHeader("From", "replaced");
        Assert.Equal("replaced", m.GetHeader("from")); // 大小写不敏感
    }

    [Fact]
    public void GetHeader_Missing_ReturnsNull()
        => Assert.Null(new SipMessage().GetHeader("Nope"));

    [Fact]
    public void ToBytes_IncludesRequestLine()
    {
        var m = new SipMessage { Method = "REGISTER", RequestUri = "sip:ims.example.net", IsRequest = true };
        m.SetHeader("Content-Length", "0");
        var text = System.Text.Encoding.UTF8.GetString(m.ToBytes());
        Assert.StartsWith("REGISTER sip:ims.example.net SIP/2.0\r\n", text);
    }
}

using VoSharp.Sip;
using VoSharp.Telephony.Dtmf;
using VoSharp.Telephony.Emergency;

namespace VoWin.Tests;

public class DtmfPayloadTests
{
    [Fact]
    public void BuildRfc4733_DigitsAndSymbols_MapsEventCode()
    {
        Assert.Equal(5, DtmfPayload.BuildRfc4733('5', false)[0]);
        Assert.Equal(10, DtmfPayload.BuildRfc4733('*', false)[0]);
        Assert.Equal(11, DtmfPayload.BuildRfc4733('#', false)[0]);
        Assert.Equal(12, DtmfPayload.BuildRfc4733('A', false)[0]);
        Assert.Equal(15, DtmfPayload.BuildRfc4733('d', false)[0]);
        Assert.Equal(0, DtmfPayload.BuildRfc4733('0', false)[0]);
        Assert.Equal(9, DtmfPayload.BuildRfc4733('9', false)[0]);
    }

    [Fact]
    public void BuildRfc4733_EndOfEventVolume_FlagsAndMask()
    {
        var start = DtmfPayload.BuildRfc4733('1', endOfEvent: false, volume: 10, duration: 160);
        Assert.Equal(0x00, start[1] & 0x80);
        Assert.Equal(10, start[1] & 0x3F);

        var end = DtmfPayload.BuildRfc4733('1', endOfEvent: true, volume: 22, duration: 240);
        Assert.Equal(0x80, end[1] & 0x80);
        Assert.Equal(22, end[1] & 0x3F);
        Assert.Equal(0x00F0, (end[2] << 8) | end[3]);
    }

    [Fact]
    public void BuildRfc4733_Unsupported_Throws()
    {
        Assert.Throws<ArgumentException>(() => DtmfPayload.BuildRfc4733('/', false));
        Assert.Throws<ArgumentException>(() => DtmfPayload.BuildRfc4733('中', false));
    }
}

public class EmergencyServiceTests
{
    [Theory]
    [InlineData("112")]
    [InlineData("911")]
    [InlineData("110")]
    [InlineData("119")]
    [InlineData("120")]
    [InlineData("999")]
    [InlineData("000")]
    [InlineData("08")]
    [InlineData("+112")]
    [InlineData(" 112 ")]
    public void IsEmergencyNumber_StandardSet_True(string number)
    {
        Assert.True(EmergencyService.IsEmergencyNumber(number));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("10086")]
    [InlineData("14")]
    [InlineData("911000")]
    [InlineData("")]
    public void IsEmergencyNumber_NonEmergency_False(string number)
    {
        Assert.False(EmergencyService.IsEmergencyNumber(number));
    }
}

public class SipMessageTests
{
    [Fact]
    public void Parse_RequestLine_AndHeaders()
    {
        const string raw = "REGISTER sip:domain.example SIP/2.0\r\n" +
                           "Via: SIP/2.0/UDP 192.168.1.1:5060\r\n" +
                           "From: <sip:user@domain.example>;tag=abc\r\n" +
                           "Call-ID: abc123\r\n" +
                           "Content-Length: 0\r\n\r\n";
        var msg = SipMessage.Parse(raw);
        Assert.True(msg.IsRequest);
        Assert.Equal("REGISTER", msg.Method);
        Assert.Equal("sip:domain.example", msg.RequestUri);
        Assert.Equal("SIP/2.0/UDP 192.168.1.1:5060", msg.GetHeader("Via"));
        Assert.Contains("tag=abc", msg.GetHeader("From"));
        Assert.Equal("abc123", msg.GetHeader("Call-ID"));
    }

    [Fact]
    public void Parse_ResponseLine_StatusCodeAndReason()
    {
        const string raw = "SIP/2.0 200 OK\r\nContent-Length: 0\r\n\r\n";
        var msg = SipMessage.Parse(raw);
        Assert.False(msg.IsRequest);
        Assert.Equal(200, msg.StatusCode);
        Assert.Equal("OK", msg.ReasonPhrase);
    }

    [Fact]
    public void CanonicalHeaderName_ShortForms()
    {
        Assert.Equal("Via", SipMessage.CanonicalHeaderName("v"));
        Assert.Equal("From", SipMessage.CanonicalHeaderName("f"));
        Assert.Equal("Call-ID", SipMessage.CanonicalHeaderName("i"));
        Assert.Equal("Content-Length", SipMessage.CanonicalHeaderName("l"));
        Assert.Equal("Contact", SipMessage.CanonicalHeaderName("m"));
        Assert.Equal("custom-header", SipMessage.CanonicalHeaderName("custom-header"));
    }

    [Fact]
    public void SetAndAddHeader_MultiValue_GetFirst()
    {
        var msg = new SipMessage();
        msg.SetHeader("Via", "one");
        msg.AddHeader("Via", "two");
        Assert.Equal("one", msg.GetHeader("Via"));
        Assert.Equal(2, msg.Headers["Via"].Count);
    }

    [Fact]
    public void CreateResponse_CopiesDialogHeaders()
    {
        var req = new SipMessage
        {
            IsRequest = true,
            Method = "INVITE",
            RequestUri = "sip:bob@example.com",
            SipVersion = "SIP/2.0"
        };
        req.SetHeader("Via", "SIP/2.0/UDP 10.0.0.1:5060");
        req.AddHeader("Via", "SIP/2.0/UDP 10.0.0.2:5060");
        req.SetHeader("From", "<sip:alice@example.com>;tag=t1");
        req.SetHeader("To", "<sip:bob@example.com>");
        req.SetHeader("Call-ID", "call-1");
        req.SetHeader("CSeq", "1 INVITE");

        var resp = req.CreateResponse(200, "OK");
        Assert.False(resp.IsRequest);
        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(2, resp.Headers["Via"].Count);
        Assert.Equal("<sip:alice@example.com>;tag=t1", resp.GetHeader("From"));
        Assert.Equal("call-1", resp.GetHeader("Call-ID"));
        Assert.Equal("0", resp.GetHeader("Content-Length"));
    }

    [Fact]
    public void ToBytes_RoundTrip_WithBody()
    {
        var msg = new SipMessage
        {
            IsRequest = true,
            Method = "MESSAGE",
            RequestUri = "sip:bob@example.com"
        };
        msg.SetHeader("From", "<sip:a@example.com>");
        msg.Body = "hello body";

        var bytes = msg.ToBytes();
        var parsed = SipMessage.Parse(bytes);
        Assert.Equal("hello body", parsed.Body);
        Assert.Equal(parsed.Body.Length.ToString(), parsed.GetHeader("Content-Length"));
    }

    [Fact]
    public void Parse_InvalidContentLength_Throws()
    {
        const string raw = "MESSAGE sip:bob@example.com SIP/2.0\r\n" +
                           "Content-Length: 999\r\n\r\n" +
                           "short";
        Assert.Throws<FormatException>(() => SipMessage.Parse(raw));
    }
}

public class SecurityAgreementBuilderTests
{
    private static SecurityProposal Proposal() =>
        new("hmac-sha-1-96", "aes-cbc", 1000, 2000, 50000, 50001);

    [Fact]
    public void FormatSpi_PadsTo10Digits()
    {
        Assert.Equal("0000000123", SecurityAgreementBuilder.FormatSpi(123));
        Assert.Equal("0000001000", SecurityAgreementBuilder.FormatSpi(1000));
    }

    [Fact]
    public void BuildSecurityClient_IncludesFirstQAndAllOffers()
    {
        var client = SecurityAgreementBuilder.BuildSecurityClient(Proposal());
        Assert.Contains("ipsec-3gpp", client);
        Assert.Contains("q=1.000", client);
        Assert.Contains("ealg=aes-cbc", client);
        Assert.Contains("spi-c=0000001000", client);
        // 3 个加密算法组合 -> 至少 3 个 offer
        Assert.True(client.Split(", ").Length >= 3);
    }

    [Fact]
    public void EvaluateSecurityServer_Matching_SelectsAndEchoes()
    {
        var proposal = Proposal();
        var server = "ipsec-3gpp;q=1.000;alg=hmac-sha-1-96;ealg=aes-cbc;" +
                     "spi-c=256;spi-s=257;port-c=6000;port-s=6001";
        var evaluation = SecurityAgreementBuilder.EvaluateSecurityServer(server, proposal);
        Assert.NotNull(evaluation.Agreement);
        Assert.Equal("hmac-sha-1-96", evaluation.Agreement.Selected.IntegrityAlgorithm);
        Assert.Equal("aes-cbc", evaluation.Agreement.Selected.EncryptionAlgorithm);
        Assert.Equal(256u, evaluation.Agreement.PcscfClientSpi);
        Assert.Equal("ipsec-3gpp;q=1.000;alg=hmac-sha-1-96;ealg=aes-cbc;spi-c=256;spi-s=257;port-c=6000;port-s=6001", evaluation.Agreement.VerifyValue);
    }

    [Fact]
    public void EvaluateSecurityServer_RejectsWrongProtocol()
    {
        var proposal = Proposal();
        var server = "ipsec-3gpp;q=1.000;prot=tcp;alg=hmac-sha-1-96;ealg=aes-cbc;" +
                     "spi-c=256;spi-s=257;port-c=6000;port-s=6001";
        var evaluation = SecurityAgreementBuilder.EvaluateSecurityServer(server, proposal);
        Assert.Null(evaluation.Agreement);
        Assert.Contains(evaluation.CandidateDiagnostics, d => d.Contains("rejected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EvaluateSecurityServer_Empty_ReturnsDiagnostic()
    {
        var evaluation = SecurityAgreementBuilder.EvaluateSecurityServer("  ", Proposal());
        Assert.Null(evaluation.Agreement);
        Assert.NotEmpty(evaluation.CandidateDiagnostics);
    }
}

public class ImsRegisterBuilderTests
{
    [Fact]
    public void FormatHost_Ipv6Brackets_OthersUnchanged()
    {
        Assert.Equal("[fe80::1]", ImsRegisterBuilder.FormatHost("fe80::1"));
        Assert.Equal("192.168.1.1", ImsRegisterBuilder.FormatHost("192.168.1.1"));
        Assert.Equal("ims.example.com", ImsRegisterBuilder.FormatHost("ims.example.com"));
        Assert.Equal(string.Empty, ImsRegisterBuilder.FormatHost(null));
        Assert.Equal("  ", ImsRegisterBuilder.FormatHost("  "));
    }

    [Fact]
    public void BuildAccessNetworkInfo_Stable_AndLocallyAdministered()
    {
        var a = ImsRegisterBuilder.BuildAccessNetworkInfo("sim-identity");
        var b = ImsRegisterBuilder.BuildAccessNetworkInfo("sim-identity");
        Assert.Equal(a, b);

        var nodePart = a.Split('=')[1];
        Assert.Equal(12, nodePart.Length);
        // 首个字节必须 locally administered（bit1=1）且非组播（bit0=0）
        var firstByte = Convert.ToByte(nodePart[..2], 16);
        Assert.Equal(0, firstByte & 0x01);
        Assert.Equal(0x02, firstByte & 0x02);
        Assert.NotEqual("000000000000", nodePart);
    }

    [Fact]
    public void ParseWwwAuthenticate_ValidDigestAka()
    {
        const string header = "Digest realm=\"ims.mnc015.mcc234.3gppnetwork.org\", nonce=\"abc123\", algorithm=AKAv1-MD5, qop=\"auth\"";
        var challenge = ImsRegisterBuilder.ParseWwwAuthenticate(header);
        Assert.NotNull(challenge);
        Assert.Equal("ims.mnc015.mcc234.3gppnetwork.org", challenge.Realm);
        Assert.Equal("abc123", challenge.Nonce);
        Assert.Equal("AKAv1-MD5", challenge.Algorithm);
        Assert.Equal("auth", challenge.Qop);
        Assert.Null(challenge.Opaque);
        Assert.False(challenge.Stale);
    }

    [Fact]
    public void ParseWwwAuthenticate_RejectsMissingOrWrongAlgorithm()
    {
        Assert.Null(ImsRegisterBuilder.ParseWwwAuthenticate("Digest realm=\"x\", nonce=\"y\""));
        Assert.Null(ImsRegisterBuilder.ParseWwwAuthenticate("Digest realm=\"x\", nonce=\"y\", algorithm=MD5"));
        Assert.Null(ImsRegisterBuilder.ParseWwwAuthenticate("Basic realm=\"x\""));
        Assert.Null(ImsRegisterBuilder.ParseWwwAuthenticate(""));
    }

    [Fact]
    public void ParseWwwAuthenticate_RejectsQopWithoutAuth()
    {
        Assert.Null(ImsRegisterBuilder.ParseWwwAuthenticate(
            "Digest realm=\"x\", nonce=\"y\", algorithm=AKAv1-MD5, qop=\"auth-int\""));
    }

    [Fact]
    public void DescribeChallenge_RedactsNonce()
    {
        const string header = "Digest realm=\"x\", nonce=\"SECRET_NONCE_VALUE\", algorithm=AKAv1-MD5";
        var description = ImsRegisterBuilder.DescribeChallenge(header);
        Assert.DoesNotContain("SECRET_NONCE_VALUE", description);
        Assert.Contains("algorithm=AKAv1-MD5", description);
        Assert.Contains("realm=x", description);
    }
}

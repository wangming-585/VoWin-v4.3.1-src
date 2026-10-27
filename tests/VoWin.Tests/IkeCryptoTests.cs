using System.Security.Cryptography;
using System.Text;
using VoSharp.Ike;

namespace VoWin.Tests;

public class ModpGroupTests
{
    [Fact]
    public void Create1024_GroupIdAndPublicLength()
    {
        using var g = ModpGroup.Create1024();
        Assert.Equal(IkeDhGroupId.Modp1024, g.GroupId);
        Assert.Equal(128, g.Public.Length);
    }

    [Fact]
    public void Create2048_PublicLength()
    {
        using var g = ModpGroup.Create2048();
        Assert.Equal(IkeDhGroupId.Modp2048, g.GroupId);
        Assert.Equal(256, g.Public.Length);
    }

    [Fact]
    public void Create_Unsupported_Throws()
    {
        Assert.Throws<NotSupportedException>(() => ModpGroup.Create(999));
    }

    [Fact]
    public void DhAgreement_BothSidesMatch()
    {
        using var a = ModpGroup.Create1024();
        using var b = ModpGroup.Create1024();
        var sa = a.ComputeSharedSecret(b.Public);
        var sb = b.ComputeSharedSecret(a.Public);
        Assert.Equal(128, sa.Length);
        Assert.Equal(sa, sb);
    }

    [Fact]
    public void DhAgreement_InvalidPeer_Throws()
    {
        using var a = ModpGroup.Create2048();
        Assert.Throws<IkeFormatException>(() => a.ComputeSharedSecret(new byte[256]));
    }
}

public class IkeCryptoTests
{
    private static IkeSuite Sha1Suite() =>
        new(IkeEncryptionId.AesCbc, 256, IkePrfId.HmacSha1, IkeIntegrityId.HmacSha1_96, IkeDhGroupId.Modp2048);

    private static byte[] Rand(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    [Fact]
    public void Prf_HmacSha1_MatchesReference()
    {
        var key = Rand(20);
        var data = Encoding.UTF8.GetBytes("IKE PRF test");
        var actual = IkeCrypto.Prf(Sha1Suite(), key, data);
        var expected = HMACSHA1.HashData(key, data);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PrfPlus_DeterministicAndLength()
    {
        var suite = IkeSuite.Preferred;
        var key = Rand(32);
        var seed = Rand(16);
        var a = IkeCrypto.PrfPlus(suite, key, seed, 100);
        var b = IkeCrypto.PrfPlus(suite, key, seed, 100);
        Assert.Equal(100, a.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public void PrfPlus_ZeroLength_Empty()
    {
        var suite = IkeSuite.Preferred;
        Assert.Empty(IkeCrypto.PrfPlus(suite, Rand(32), Rand(16), 0));
    }

    [Fact]
    public void PrfPlus_NegativeLength_Throws()
    {
        var suite = IkeSuite.Preferred;
        Assert.Throws<ArgumentOutOfRangeException>(() => IkeCrypto.PrfPlus(suite, Rand(32), Rand(16), -1));
    }

    [Fact]
    public void DeriveIkeKeys_Deterministic_Lengths()
    {
        var suite = IkeSuite.Preferred;
        var shared = Rand(32);
        var ni = Rand(16);
        var nr = Rand(16);
        const ulong spiI = 0x1111111111111111UL;
        const ulong spiR = 0x2222222222222222UL;

        var first = IkeCrypto.DeriveIkeKeys(suite, shared, ni, nr, spiI, spiR);
        var second = IkeCrypto.DeriveIkeKeys(suite, shared, ni, nr, spiI, spiR);

        Assert.Equal(suite.PrfLength, first.SkD.Length);
        Assert.Equal(suite.IntegrityKeyLength, first.SkAi.Length);
        Assert.Equal(suite.IntegrityKeyLength, first.SkAr.Length);
        Assert.Equal(suite.EncryptionKeyLength, first.SkEi.Length);
        Assert.Equal(suite.EncryptionKeyLength, first.SkEr.Length);
        Assert.Equal(suite.PrfLength, first.SkPi.Length);
        Assert.Equal(suite.PrfLength, first.SkPr.Length);

        Assert.Equal(first.SkD, second.SkD);
        Assert.Equal(first.SkAi, second.SkAi);
        Assert.Equal(first.SkEi, second.SkEi);
    }

    [Fact]
    public void IntegrityMac_OutputLengthEqualsChecksum()
    {
        var suite = IkeSuite.Preferred;
        var key = Rand(32);
        var data = Rand(64);
        var mac = IkeCrypto.IntegrityMac(suite, key, data);
        Assert.Equal(suite.ChecksumLength, mac.Length);
    }
}

public class IkeWireTests
{
    [Fact]
    public void Message_RoundTrip_HeaderOnly()
    {
        var msg = new IkeWire.IkeMessage
        {
            InitiatorSpi = 0x1122334455667788UL,
            ResponderSpi = 0x99AABBCCDDEEFF00UL,
            Exchange = IkeExchangeType.IkeSaInit,
            Flags = IkeFlags.Initiator,
            MessageId = 7
        };

        var bytes = IkeWire.SerializeMessage(msg);
        var parsed = IkeWire.ParseMessage(bytes);

        Assert.Equal(msg.InitiatorSpi, parsed.InitiatorSpi);
        Assert.Equal(msg.ResponderSpi, parsed.ResponderSpi);
        Assert.Equal(msg.Exchange, parsed.Exchange);
        Assert.Equal(msg.Flags, parsed.Flags);
        Assert.Equal(msg.MessageId, parsed.MessageId);
        Assert.Equal(msg.Version, parsed.Version);
        Assert.Empty(parsed.Payloads);
    }

    [Fact]
    public void Message_ParsePayload_Nonce_ReRoundTrip()
    {
        var body = new byte[16];
        RandomNumberGenerator.Fill(body);
        var msg = new IkeWire.IkeMessage
        {
            InitiatorSpi = 1,
            ResponderSpi = 2,
            Exchange = IkeExchangeType.IkeSaInit,
            Flags = IkeFlags.Initiator,
            MessageId = 1
        };
        msg.Payloads.Add(new IkePayload(IkePayloadType.Nonce, body));

        var bytes = IkeWire.SerializeMessage(msg);
        var parsed = IkeWire.ParseMessage(bytes);
        Assert.Single(parsed.Payloads);
        Assert.Equal(IkePayloadType.Nonce, parsed.Payloads[0].Type);
        Assert.Equal(body, parsed.Payloads[0].Body);
    }

    [Fact]
    public void SerializePayloadChain_Parse_RoundTrip()
    {
        var p1 = new IkePayload(IkePayloadType.Nonce, new byte[] { 1, 2, 3, 4 });
        var p2 = new IkePayload(IkePayloadType.VendorId, new byte[] { 9, 8, 7, 6, 5 }, Critical: true);
        var chain = IkeWire.SerializePayloadChain(new[] { p1, p2 });
        var parsed = IkeWire.ParsePayloadChain(chain, IkePayloadType.Nonce);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(IkePayloadType.Nonce, parsed[0].Type);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, parsed[0].Body);
        Assert.Equal(IkePayloadType.VendorId, parsed[1].Type);
        Assert.True(parsed[1].Critical);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, parsed[1].Body);
    }

    [Fact]
    public void Proposals_RoundTrip()
    {
        var proposal = new IkeProposal
        {
            ProposalNumber = 1,
            Protocol = IkeProtocolId.Ike,
            Transforms = new List<IkeTransform>
            {
                new(IkeTransformType.Encryption, IkeEncryptionId.AesCbc, 256),
                new(IkeTransformType.Prf, IkePrfId.HmacSha2_256),
                new(IkeTransformType.Integrity, IkeIntegrityId.HmacSha2_256_128),
                new(IkeTransformType.DiffieHellman, IkeDhGroupId.Modp2048)
            }
        };

        var bytes = IkeWire.EncodeProposals(new[] { proposal });
        var decoded = IkeWire.DecodeProposals(bytes);

        Assert.Single(decoded);
        Assert.Equal(1, decoded[0].ProposalNumber);
        Assert.Equal(IkeProtocolId.Ike, decoded[0].Protocol);
        Assert.Equal(4, decoded[0].Transforms.Count);
        Assert.Equal(IkeTransformType.Encryption, decoded[0].Transforms[0].Type);
        Assert.Equal(IkeEncryptionId.AesCbc, decoded[0].Transforms[0].Id);
        Assert.Equal((ushort)256, decoded[0].Transforms[0].KeyLengthBits);
    }

    [Fact]
    public void ParseMessage_TooShort_Throws()
    {
        Assert.Throws<IkeFormatException>(() => IkeWire.ParseMessage(new byte[16]));
        Assert.Throws<IkeFormatException>(() => IkeWire.ParseMessage(Array.Empty<byte>()));
    }

    [Fact]
    public void SerializePayloadChain_Empty_ReturnsZeroLength()
    {
        var bytes = IkeWire.SerializePayloadChain(Array.Empty<IkePayload>());
        Assert.Empty(bytes);
    }
}

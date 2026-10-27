using VoSharp.Sim;
using Xunit;

namespace VoWin.Tests;

public class ImeiGeneratorTests
{
    [Theory]
    [InlineData(ImeiGenerator.DefaultIphoneProTac)]
    [InlineData("35612091")] // iPhone 17 Pro Max
    [InlineData("35419826")]
    public void Generate_ProducesLuhnValid15Digits(string tac)
    {
        var imei = ImeiGenerator.Generate(tac);
        Assert.Equal(15, imei.Length);
        Assert.All(imei, c => Assert.True(char.IsDigit(c)));
        Assert.StartsWith(tac, imei);
        Assert.True(ImeiGenerator.ValidateLuhn(imei));
    }

    [Fact]
    public void LuhnCheckDigit_MatchesKnownReferenceValue()
    {
        // Known-good IMEI from public TAC reference data (Swappa listing 357431261506766).
        Assert.True(ImeiGenerator.ValidateLuhn("357431261506766"));
        Assert.Equal('6', ImeiGenerator.LuhnCheckDigit("35743126150676"));
    }

    [Fact]
    public void Generate_RejectsInvalidTac()
    {
        Assert.Throws<ArgumentException>(() => ImeiGenerator.Generate("12345"));
        Assert.Throws<ArgumentException>(() => ImeiGenerator.Generate("1234567a"));
        Assert.Throws<ArgumentException>(() => ImeiGenerator.Generate(""));
    }

    [Fact]
    public void ImeisvFromImei_ReplacesCheckDigitWithSoftwareVersion()
    {
        var imei = "354996261234567";
        Assert.Equal("3549962612345623", ImeiGenerator.ImeisvFromImei(imei, "23"));
        Assert.Equal("3549962612345607", ImeiGenerator.ImeisvFromImei(imei, "7"));
    }

    [Fact]
    public void ValidateLuhn_RejectsMalformedInput()
    {
        Assert.False(ImeiGenerator.ValidateLuhn(null));
        Assert.False(ImeiGenerator.ValidateLuhn("12345"));
        Assert.False(ImeiGenerator.ValidateLuhn("354996261234568")); // wrong check digit
    }
}

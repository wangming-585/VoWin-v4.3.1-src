using VoWin.Services.RemoteControl;

namespace VoWin.Tests;

public class RemoteMessageMarkdownTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain text", "plain text")]
    [InlineData("a*b", @"a\*b")]
    [InlineData("a`b", @"a\`b")]
    [InlineData("_[x]_{}", @"\_\[x\]\_\{\}")]
    [InlineData("#head", @"\#head")]
    [InlineData("1. list", @"1\. list")]
    [InlineData("a|b>c!d", @"a\|b\>c\!d")]
    public void Escape_Cases(string? input, string expected)
        => Assert.Equal(expected, RemoteMessageMarkdown.Escape(input));

    [Fact]
    public void Escape_NormalizesNewlines()
    {
        Assert.Equal("a\nb", RemoteMessageMarkdown.Escape("a\r\nb"));
        Assert.Equal("a\nb", RemoteMessageMarkdown.Escape("a\rb"));
    }

    [Fact]
    public void Escape_中文不受影响()
        => Assert.Equal("短信发送成功", RemoteMessageMarkdown.Escape("短信发送成功"));

    [Theory]
    [InlineData(null, "`-`")]
    [InlineData("OK", "`OK`")]
    [InlineData("line1\nline2", "`line1 line2`")]
    [InlineData("line1\r\nline2", "`line1 line2`")]
    public void Code_Cases(string? input, string expected)
        => Assert.Equal(expected, RemoteMessageMarkdown.Code(input));

    [Fact]
    public void Code_ReplacesBacktickWithModifier()
        => Assert.Equal("`aˋb`", RemoteMessageMarkdown.Code("a`b"));

    [Fact]
    public void Quote_PrefixesEveryLine()
    {
        var q = RemoteMessageMarkdown.Quote("第一行\n第二行*重点*");
        Assert.Equal("> 第一行\n> 第二行\\*重点\\*", q);
    }

    [Fact]
    public void Quote_Null_SingleEmptyPrefixedLine()
        => Assert.Equal("> ", RemoteMessageMarkdown.Quote(null));

    [Fact]
    public void Error_TitleOnly()
        => Assert.Equal("❌ **切卡失败**", RemoteMessageMarkdown.Error("切卡失败"));

    [Fact]
    public void Error_WithDetail_QuotesDetail()
    {
        var e = RemoteMessageMarkdown.Error("失败", "原因A\n原因B");
        Assert.StartsWith("❌ **失败**", e);
        Assert.Contains("> 原因A", e);
        Assert.Contains("> 原因B", e);
    }

    [Fact]
    public void Error_EscapesTitlePunctuation()
        => Assert.Equal(@"❌ **404 \(not found\)**", RemoteMessageMarkdown.Error("404 (not found)"));
}

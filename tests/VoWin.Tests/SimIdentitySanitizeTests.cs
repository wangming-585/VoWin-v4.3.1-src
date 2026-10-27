using VoSharp.Sim;
using Xunit;

namespace VoWin.Tests;

/// <summary>
/// EF.IMSI 长度 octet 被误解码为前导数字的污染修复测试。
/// 实际案例：Lebara UK 卡 IMSI 被读成 9234870100172268（正确 234870100172268），
/// 旧卡 234159611957757 被读成 9234159611957757。
/// </summary>
public class SimIdentitySanitizeTests
{
    [Theory]
    [InlineData("9234870100172268", "234870100172268")]   // Lebara UK 234-87（当前卡）
    [InlineData("9234159611957757", "234159611957757")]   // Lebara 234-15（旧卡实测）
    [InlineData("9460078753000026", "460078753000026")]   // 中国 460-00 前置 9
    public void SanitizeImsi_StripsSpuriousLengthDigit(string polluted, string expected)
    {
        Assert.Equal(expected, SimIdentity.SanitizeImsi(polluted));
    }

    [Theory]
    [InlineData("234870100172268")]      // 正确 15 位原样保留
    [InlineData("234159611957757")]
    [InlineData("460078753000026")]
    [InlineData("310260123456789")]      // 15 位美国，1 不在 8/9 触发集
    [InlineData("923487010017226")]      // 15 位且 9 开头：长度不符不剥离
    public void SanitizeImsi_KeepsValidImsi(string imsi)
    {
        Assert.Equal(imsi, SimIdentity.SanitizeImsi(imsi));
    }

    [Fact]
    public void FromImsiAndIccid_PollutedImsi_YieldsCorrectPlmn()
    {
        var id = SimIdentity.FromImsiAndIccid("9234870100172268", "89441600001001722687");
        Assert.Equal("234870100172268", id.Imsi);
        Assert.Equal("234", id.Mcc);
        Assert.Equal("87", id.Mnc);
    }

    [Fact]
    public void IsAssignedMcc_RejectsUnassigned900Band()
    {
        Assert.False(SimIdentity.IsAssignedMcc("923"));
        Assert.False(SimIdentity.IsAssignedMcc("946"));
        Assert.True(SimIdentity.IsAssignedMcc("234"));
        Assert.True(SimIdentity.IsAssignedMcc("460"));
    }
}

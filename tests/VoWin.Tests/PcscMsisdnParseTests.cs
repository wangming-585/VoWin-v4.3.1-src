using System.Reflection;
using VoSharp.Sim;
using Xunit;

namespace VoWin.Tests;

/// <summary>
/// 锁定 EF_MSISDN 记录解析（alpha 前缀跳过、BCD 解码、TON 国际号加 +、空文件返回 null）。
/// </summary>
public class PcscMsisdnParseTests
{
    private static string? Parse(byte[] record)
    {
        var method = typeof(PcscAkaProvider).GetMethod("ParseMsisdnRecord",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, new object[] { record });
    }

    [Fact]
    public void InternationalNumber_WithAlphaPrefix()
    {
        // 30 字节记录：16 字节 alpha + len(0x07=TON/NPI+6 字节 BCD) + 91 + 10 BCD
        var record = new byte[30];
        Array.Fill(record, (byte)0xFF);
        record[16] = 0x07;
        record[17] = 0x91; // TON=international
        // +447700900123 → 数字对 44 77 00 90 01 23 → 半字节反转：44 77 00 09 10 32
        record[18] = 0x44; record[19] = 0x77; record[20] = 0x00;
        record[21] = 0x09; record[22] = 0x10; record[23] = 0x32;

        Assert.Equal("+447700900123", Parse(record));
    }

    [Fact]
    public void UnassignedFile_AllFF_ReturnsNull()
    {
        var record = new byte[30];
        Array.Fill(record, (byte)0xFF);
        Assert.Null(Parse(record));
    }

    [Fact]
    public void ZeroLength_ReturnsNull()
    {
        var record = new byte[30];
        Array.Fill(record, (byte)0xFF);
        record[16] = 0x00;
        Assert.Null(Parse(record));
    }

    [Fact]
    public void NationalNumber_NoPlusPrefix()
    {
        var record = new byte[14];
        Array.Fill(record, (byte)0x00);
        record[0] = 0x04;      // len = TON/NPI + 3 字节 BCD
        record[1] = 0x81;      // TON=unknown → 不加 +
        // 123456 → 数字对 12 34 56 → 半字节反转：21 43 65
        record[2] = 0x21; record[3] = 0x43; record[4] = 0x65;

        Assert.Equal("123456", Parse(record));
    }
}

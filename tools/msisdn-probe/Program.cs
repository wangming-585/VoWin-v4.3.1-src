using VoSharp.Sim;
using System.Text;
using VoSharp.Sim.Pcsc;

// 用 WinSCard 直接枚举读卡器
IntPtr ctx = IntPtr.Zero;
if (WinSCard.SCardEstablishContext(0, IntPtr.Zero, IntPtr.Zero, out ctx) != 0)
{
    Console.WriteLine("EstablishContext failed");
    return;
}
uint len = 0;
var res = WinSCard.SCardListReaders(ctx, null, null, ref len);
var buffer = new byte[len * 2];
res = WinSCard.SCardListReaders(ctx, null, buffer, ref len);
var str = Encoding.Unicode.GetString(buffer, 0, (int)len * 2);
var readers = str.Split('\0', StringSplitOptions.RemoveEmptyEntries);
Console.WriteLine($"readers({readers.Length}): {string.Join(" | ", readers)}");

foreach (var r in readers)
{
    try
    {
        using var p = new PcscAkaProvider(r);
        var iccid = await p.ReadIccidAsync();
        var imsi = await p.ReadImsiAsync();
        var msisdn = await p.ReadMsisdnAsync();
        Console.WriteLine($"[{r}] ICCID={iccid} IMSI={imsi} MSISDN={(msisdn is null ? "NULL" : msisdn)}");
    }
    catch (Exception ex) { Console.WriteLine($"[{r}] ERR: {ex.Message}"); }
}

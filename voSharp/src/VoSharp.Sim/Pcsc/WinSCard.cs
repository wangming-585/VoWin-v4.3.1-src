using System.Runtime.InteropServices;
using System.Text;
using VoSharp.Common.Utils;

namespace VoSharp.Sim.Pcsc;

public static partial class WinSCard
{
    public const uint SCARD_SCOPE_USER = 0;
    public const uint SCARD_SHARE_SHARED = 2;
    public const uint SCARD_PROTOCOL_T0 = 1;
    public const uint SCARD_PROTOCOL_T1 = 2;
    public const uint SCARD_LEAVE_CARD = 0;
    public const uint SCARD_RESET_CARD = 1;
    public const uint SCARD_UNPOWER_CARD = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct SCARD_IO_REQUEST
    {
        public uint dwProtocol;
        public uint cbPciLength;
    }

    [LibraryImport("winscard.dll", SetLastError = true)]
    public static partial int SCardEstablishContext(uint dwScope, IntPtr pvReserved1, IntPtr pvReserved2, out IntPtr phContext);

    [LibraryImport("winscard.dll", SetLastError = true)]
    public static partial int SCardReleaseContext(IntPtr hContext);

    [DllImport("winscard.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int SCardListReaders(IntPtr hContext, string? mszGroups, byte[]? mszReaders, ref uint pcchReaders);

    [DllImport("winscard.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int SCardConnect(IntPtr hContext, string szReader, uint dwShareMode, uint dwPreferredProtocols, out IntPtr phCard, out uint pdwActiveProtocol);

    [LibraryImport("winscard.dll", SetLastError = true)]
    public static partial int SCardDisconnect(IntPtr hCard, uint dwDisposition);

    [LibraryImport("winscard.dll", SetLastError = true)]
    public static partial int SCardTransmit(IntPtr hCard, ref SCARD_IO_REQUEST pioSendPci, byte[] pbSendBuffer, uint cbSendLength, IntPtr pioRecvPci, byte[] pbRecvBuffer, ref uint pcbRecvLength);
}

public class PcscReader : IDisposable
{
    private IntPtr _context = IntPtr.Zero;
    private IntPtr _card = IntPtr.Zero;
    private uint _activeProtocol;

    public bool Initialize()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            return false;

        int res = WinSCard.SCardEstablishContext(WinSCard.SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out _context);
        return res == 0;
    }

    public string[] ListReaders()
    {
        if (_context == IntPtr.Zero && !Initialize())
            return Array.Empty<string>();

        uint len = 0;
        int res = WinSCard.SCardListReaders(_context, null, null, ref len);
        if (res != 0 || len == 0)
            return Array.Empty<string>();

        var buffer = new byte[len * 2];
        res = WinSCard.SCardListReaders(_context, null, buffer, ref len);
        if (res != 0)
            return Array.Empty<string>();

        var str = Encoding.Unicode.GetString(buffer, 0, (int)len * 2);
        return str.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public bool Connect(string readerName)
    {
        if (_context == IntPtr.Zero && !Initialize())
            return false;

        int res = WinSCard.SCardConnect(
            _context,
            readerName,
            WinSCard.SCARD_SHARE_SHARED,
            WinSCard.SCARD_PROTOCOL_T0 | WinSCard.SCARD_PROTOCOL_T1,
            out _card,
            out _activeProtocol
        );
        if (res != 0)
        {
            System.Diagnostics.Trace.WriteLine($"[PCSC] SCardConnect FAILED reader='{readerName}' res=0x{res:X8} ({res})");
        }
        return res == 0;
    }

    /// <summary>
    /// Drops the current card handle and opens a fresh session, without changing
    /// the card's power state.
    /// </summary>
    /// <remarks>
    /// Used after an eUICC profile switch: the switch itself power-cycles the card
    /// through its own handle, which silently invalidates every other handle on
    /// the same card (our AKA provider keeps answering "no response" until it
    /// reconnects). Reconnecting is enough here - unpowering again would restart
    /// the very REFRESH procedure the switch just finished.
    /// </remarks>
    public bool Reconnect(string readerName)
    {
        try { Disconnect(WinSCard.SCARD_LEAVE_CARD); }
        catch { /* the handle may already be invalid after a foreign power cycle */ }

        if (_context == IntPtr.Zero && !Initialize())
            return false;

        return Connect(readerName);
    }

    /// <summary>
    /// Power-cycles the card and reconnects to it.
    /// </summary>
    /// <remarks>
    /// After an eUICC profile switch the card starts a SIM REFRESH procedure and
    /// waits for the terminal to acknowledge it. A PC/SC reader never sends that
    /// acknowledgement, so the card stays locked and answers 6985 / 91 0B to
    /// every command until power is removed and re-applied - exactly what a
    /// phone does when it reboots the SIM. A plain reconnect (SCARD_LEAVE_CARD)
    /// only drops the logical channel and does not clear the lock, so the
    /// caller needs this explicit power cycle to recover.
    /// </remarks>
    /// <returns>True when the card was power-cycled and reconnected successfully.</returns>
    public bool PowerCycleAndReconnect(string readerName)
    {
        // Drop the current handle with an explicit unpower disposition. This
        // both releases the card and forces the reader to re-apply power.
        try { Disconnect(WinSCard.SCARD_UNPOWER_CARD); }
        catch { /* the handle may already be gone */ }

        // A short settling delay is required: the reader needs a moment to
        // re-apply power and the card needs to finish its own reset before it
        // answers SELECT again.
        Thread.Sleep(250);

        if (_context == IntPtr.Zero && !Initialize())
            return false;

        return Connect(readerName);
    }

    public byte[]? TransmitApdu(byte[] apdu)
    {
        if (_card == IntPtr.Zero)
            return null;

        var ioRequest = new WinSCard.SCARD_IO_REQUEST
        {
            dwProtocol = _activeProtocol,
            cbPciLength = (uint)Marshal.SizeOf<WinSCard.SCARD_IO_REQUEST>()
        };

        var recvBuffer = new byte[512];
        uint recvLen = (uint)recvBuffer.Length;

        int res = WinSCard.SCardTransmit(
            _card,
            ref ioRequest,
            apdu,
            (uint)apdu.Length,
            IntPtr.Zero,
            recvBuffer,
            ref recvLen
        );

        if (res != 0) return null;

        var result = new byte[recvLen];
        Array.Copy(recvBuffer, result, recvLen);
        return result;
    }

    public void Disconnect()
    {
        if (_card != IntPtr.Zero)
        {
            WinSCard.SCardDisconnect(_card, WinSCard.SCARD_LEAVE_CARD);
            _card = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Drops the card handle with an explicit disposition. SCARD_UNPOWER_CARD
    /// forces a real power cycle, which is the only way to clear a SIM REFRESH
    /// that is waiting for a terminal response nobody will send.
    /// </summary>
    public void Disconnect(uint disposition)
    {
        if (_card != IntPtr.Zero)
        {
            WinSCard.SCardDisconnect(_card, disposition);
            _card = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Disconnect();
        if (_context != IntPtr.Zero)
        {
            WinSCard.SCardReleaseContext(_context);
            _context = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }
}

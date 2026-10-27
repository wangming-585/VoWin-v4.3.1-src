using System.Security.Cryptography;

namespace VoSharp.Sim;

/// <summary>
/// Generates syntactically valid IMEI/IMEISV values for a given Type Allocation Code.
/// IMEI = TAC(8) + SNR(6) + Luhn check digit(1); IMEISV = TAC(8) + SNR(6) + SV(2), no check digit
/// (3GPP TS 23.003 §6.2.1/§6.3). Generated values are random identifiers for lab use only —
/// they carry no real device binding.
/// </summary>
public static class ImeiGenerator
{
    /// <summary>Default TAC: iPhone 17 Pro (A3256). Replace once an iPhone 18 Pro TAC is published.</summary>
    public const string DefaultIphoneProTac = "35499626";

    /// <summary>Generates a 15-digit Luhn-valid IMEI for the supplied 8-digit TAC.</summary>
    public static string Generate(string tac)
    {
        var t = (tac ?? string.Empty).Trim();
        if (t.Length != 8 || !t.All(char.IsDigit))
            throw new ArgumentException("TAC must be exactly 8 digits.", nameof(tac));

        Span<char> digits = stackalloc char[15];
        t.AsSpan().CopyTo(digits);
        Span<char> digitsChars = stackalloc char[6];
        foreach (ref char c in digitsChars) c = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        digitsChars.CopyTo(digits.Slice(8, 6));
        digits[14] = LuhnCheckDigit(digits[..14].ToString());
        return new string(digits);
    }

    /// <summary>Builds the 16-digit IMEISV (TAC + SNR + 2-digit software version).</summary>
    public static string ImeisvFromImei(string imei, string softwareVersion = "23")
    {
        var v = (softwareVersion ?? string.Empty).Trim().PadLeft(2, '0');
        if (v.Length != 2 || !v.All(char.IsDigit))
            throw new ArgumentException("Software version must be 1-2 digits.", nameof(softwareVersion));
        var imsi = (imei ?? string.Empty).Trim();
        if (imsi.Length != 15 || !imsi.All(char.IsDigit))
            throw new ArgumentException("IMEI must be exactly 15 digits.", nameof(imei));
        return imsi[..14] + v;
    }

    /// <summary>Computes the Luhn check digit over a 14-digit TAC+SNR string.</summary>
    public static char LuhnCheckDigit(string fourteenDigits)
    {
        var s = (fourteenDigits ?? string.Empty).Trim();
        if (s.Length != 14 || !s.All(char.IsDigit))
            throw new ArgumentException("Expected exactly 14 digits.", nameof(fourteenDigits));
        return LuhnCheckDigitCore(s);
    }

    /// <summary>Validates that a 15-digit IMEI satisfies the Luhn checksum.</summary>
    public static bool ValidateLuhn(string? imei)
    {
        var s = (imei ?? string.Empty).Trim();
        return s.Length == 15 && s.All(char.IsDigit) && LuhnCheckDigitCore(s[..14]) == s[14];
    }

    private static char LuhnCheckDigitCore(string fourteenDigits)
    {
        // 3GPP TS 23.003: Luhn over TAC+SNR where the check digit position counts as position 1.
        int sum = 0;
        for (int i = 0; i < fourteenDigits.Length; i++)
        {
            int d = fourteenDigits[14 - 1 - i] - '0';
            if (i % 2 == 0) // doubling starts at the digit immediately left of the check digit
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return (char)('0' + (10 - sum % 10) % 10);
    }
}

namespace VoSharp.Sim;

public record SimIdentity(
    string Imsi,
    string Iccid,
    string Mcc,
    string Mnc,
    string OperatorName,
    string? PhoneNumber = null,
    bool IsHomePlmnAuthoritative = false,
    IReadOnlyList<string>? HomePlmns = null,
    string? ReportedImsi = null,
    string? PermanentImsi = null,
    string ImsiSource = "unknown",
    bool HasPlmnConflict = false
)
{
    /// <summary>
    /// 剥离 BCD 长度 octet 被误当数字解码产生的错误前导位。
    /// EF.IMSI 首字节是长度（15 位 IMSI 为 0x08、16 位为 0x09）；个别读取
    /// 链路会把该 octet 一并解码，得到 '8'+15 位或 '9'+15 位的 16 位串。
    /// 9xx 段 MCC（900-999）绝大部分未分配给国家，而剥离首位后若得到
    /// 合法 MCC，则可判定为污染并还原。
    /// </summary>
    public static string SanitizeImsi(string imsi)
    {
        imsi = imsi.Trim();
        // 16 位且首数字恰为 8/9（EF.IMSI 长度 octet 的典型值）且去掉后为合法 MCC。
        if (imsi.Length == 16 && (imsi[0] == '8' || imsi[0] == '9')
            && IsAssignedMcc(imsi.Substring(1, 3)))
        {
            System.Diagnostics.Trace.WriteLine(
                $"[SIM] IMSI sanitized: {imsi[..4]}**** -> {imsi.Substring(1, 4)}**** (stripped spurious length-digit prefix '{imsi[0]}')");
            return imsi[1..];
        }
        return imsi;
    }

    /// <summary>ITU E.212 已分配的 MCC 首位数字段（保守判断，覆盖所有在用国家）。900-999 为测试/未分配段。</summary>
    public static bool IsAssignedMcc(string mcc) =>
        mcc.Length == 3 && mcc.All(char.IsAsciiDigit)
        && mcc[0] is >= '2' and <= '7' or '0';

    public static SimIdentity FromImsiAndIccid(
        string imsi,
        string iccid,
        string? opName = null,
        string? phoneNumber = null,
        int? mncLength = null,
        IReadOnlyList<string>? homePlmns = null,
        string? reportedImsi = null,
        string? permanentImsi = null,
        string? imsiSource = null,
        bool hasPlmnConflict = false)
    {
        imsi = SanitizeImsi(imsi);
        iccid = iccid.Trim();
        var mcc = imsi.Length >= 3 ? imsi[..3] : "";
        var validatedMncLength = mncLength is 2 or 3 ? mncLength.Value : 2;
        var mnc = imsi.Length >= 3 + validatedMncLength
            ? imsi.Substring(3, validatedMncLength)
            : "";
        var operatorName = string.IsNullOrWhiteSpace(opName)
            ? $"PLMN {mcc}-{mnc}"
            : opName.Trim();
        return new SimIdentity(
            imsi,
            iccid,
            mcc,
            mnc,
            operatorName,
            phoneNumber,
            IsHomePlmnAuthoritative: mncLength is 2 or 3,
            HomePlmns: homePlmns?.Distinct(StringComparer.Ordinal).ToArray(),
            ReportedImsi: string.IsNullOrWhiteSpace(reportedImsi) ? null : reportedImsi.Trim(),
            PermanentImsi: string.IsNullOrWhiteSpace(permanentImsi) ? null : permanentImsi.Trim(),
            ImsiSource: string.IsNullOrWhiteSpace(imsiSource) ? "unknown" : imsiSource.Trim(),
            HasPlmnConflict: hasPlmnConflict);
    }
}

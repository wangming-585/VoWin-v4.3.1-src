using CommunityToolkit.Mvvm.ComponentModel;
using VoWin.Helpers;

namespace VoWin.Models;

/// <summary>
/// Highest-priority VoWiFi egress rule for one physical SIM/eSIM profile.
/// The presence of a row is significant: a row without a node explicitly
/// forces DIRECT and therefore still overrides the IMSI MCC/PLMN rule.
/// </summary>
public partial class IccidRouteModel : ObservableObject
{
    [ObservableProperty]
    private string _iccid = string.Empty;

    [ObservableProperty]
    private string? _cardName;

    [ObservableProperty]
    private string? _imsiSnapshot;

    [ObservableProperty]
    private string? _phoneNumber;

    [ObservableProperty]
    private string? _proxyNodeId;

    [ObservableProperty]
    private string? _proxyUrl;

    [ObservableProperty]
    private string _proxyNodeName = "直连模式 (Direct)";

    [ObservableProperty]
    private DateTime _updatedAt = DateTime.Now;

    partial void OnProxyNodeIdChanged(string? value) => OnPropertyChanged(nameof(IsDirect));
    partial void OnProxyUrlChanged(string? value) => OnPropertyChanged(nameof(IsDirect));
    partial void OnCardNameChanged(string? value) => OnPropertyChanged(nameof(DisplayName));
    partial void OnImsiSnapshotChanged(string? value)
    {
        OnPropertyChanged(nameof(ImsiDisplay));
        OnPropertyChanged(nameof(CountryDisplay));
        OnPropertyChanged(nameof(CountryNameDisplay));
    }
    partial void OnPhoneNumberChanged(string? value) => OnPropertyChanged(nameof(PhoneDisplay));

    public bool IsDirect => string.IsNullOrWhiteSpace(ProxyNodeId) && string.IsNullOrWhiteSpace(ProxyUrl);

    public string DisplayName => string.IsNullOrWhiteSpace(CardName) ? "未命名 SIM" : CardName;

    public string ImsiDisplay => string.IsNullOrWhiteSpace(ImsiSnapshot) ? "IMSI 未记录" : $"IMSI {ImsiSnapshot}";

    public string PhoneDisplay => string.IsNullOrWhiteSpace(PhoneNumber) ? "号码未提供" : PhoneNumber;

    /// <summary>由 IMSI MCC 派生的国旗+国家码；无 IMSI 或未识别 MCC 时为 "--"。</summary>
    public string CountryDisplay
    {
        get
        {
            var meta = FindCountry();
            if (meta == null) return "--";
            // Flag 可能为空（查表未提供 emoji），避免产生前导空格。
            return string.IsNullOrEmpty(meta.Flag) ? meta.Code : $"{meta.Flag} {meta.Code}";
        }
    }

    /// <summary>仅国家名（如“英国”），未识别时为 null。侧栏备注用它，不再拼接卡名/尾号。</summary>
    public string? CountryShortName
    {
        get
        {
            var meta = FindCountry();
            return string.IsNullOrWhiteSpace(meta?.Name) ? null : meta!.Name;
        }
    }

    /// <summary>国家完整展示（tooltip 用），未识别时给出解释文案。</summary>
    public string CountryNameDisplay
    {
        get
        {
            var meta = FindCountry();
            if (meta != null)
            {
                var flag = string.IsNullOrEmpty(meta.Flag) ? string.Empty : meta.Flag + " ";
                return $"{flag}{meta.Name} ({meta.Code})";
            }
            return string.IsNullOrWhiteSpace(ImsiSnapshot)
                ? "无 IMSI 快照，无法推导国家"
                : $"IMSI MCC {ImsiSnapshot[..Math.Min(3, ImsiSnapshot.Length)]} 未识别";
        }
    }

    private CountryMeta? FindCountry() =>
        string.IsNullOrWhiteSpace(ImsiSnapshot) || ImsiSnapshot.Length < 3
            ? null
            : MccCountryHelper.FindByMcc(ImsiSnapshot[..3]);
}

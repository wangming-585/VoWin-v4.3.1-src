using CommunityToolkit.Mvvm.ComponentModel;

namespace VoWin.Models
{
    public partial class SimPreferenceModel : ObservableObject
    {
        [ObservableProperty]
        private string _iccid = string.Empty;

        [ObservableProperty]
        private string? _imsi;

        [ObservableProperty]
        private string? _cardNickname;

        [ObservableProperty]
        private bool _defaultFlightMode = false;

        [ObservableProperty]
        private bool _defaultVoWifi = false;

        [ObservableProperty]
        private bool _defaultCellularData = false;

        [ObservableProperty]
        private bool _defaultDataRoaming = false;

        [ObservableProperty]
        private string? _dedicatedProxyUrl;

        [ObservableProperty]
        private string? _customEpdg;

        /// <summary>自动解析的显示名（AT+CNUM → IMS P-Associated-URI → 运营商+ICCID 尾号），按 ICCID 持久化；手填 CardNickname 优先。</summary>
        [ObservableProperty]
        private string? _displayName;

        [ObservableProperty]
        private DateTime _lastSeenAt = DateTime.Now;
    }
}

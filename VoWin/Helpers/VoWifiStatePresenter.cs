using VoSharp.StateMachine;
using VoSharp.Telephony.VoWifi;

namespace VoWin.Helpers;

/// <summary>
/// Maps the low-level VoWiFi state machine onto the nine user-facing dashboard
/// states from the UX brief. Display only: never feeds back into routing or
/// protocol decisions. Unknown combinations fall back to a neutral
/// "working on it" presentation instead of guessing a specific stage.
/// </summary>
public static class VoWifiStatePresenter
{
    public enum DashboardPhase
    {
        NoCard,          // 未检测到卡片
        WaitingSim,      // 等待 SIM 就绪
        ResolvingEpdg,   // 正在解析 ePDG
        Authenticating,  // 正在进行身份鉴权
        BuildingTunnel,  // 正在建立 IPsec 隧道
        RegisteringIms,  // 正在注册 IMS
        Connected,       // 已连接 / 已注册
        Failed,          // 连接失败
        Stopped          // 已手动停止
    }

    public sealed record Presentation(
        DashboardPhase Phase,
        string Title,
        string Description,
        string Tone) // success | info | warning | danger | muted
    {
        public bool IsBusy => Tone == "info";
        public bool IsSuccess => Tone == "success";
        public bool IsFailure => Tone == "danger";
    }

    /// <summary>
    /// Resolves the dashboard phase. Callers pass live slot/SIM facts so the
    /// two pre-flight states (no card / SIM not ready) reflect physical
    /// reality rather than the VoWiFi state machine alone.
    /// </summary>
    public static DashboardPhase ResolvePhase(
        VoWifiState voWifiState,
        bool anySlotPresent,
        bool simReady,
        bool userStopped)
    {
        if (!anySlotPresent)
            return DashboardPhase.NoCard;

        if (!simReady && voWifiState == VoWifiState.Disconnected)
            return userStopped ? DashboardPhase.Stopped : DashboardPhase.WaitingSim;

        return voWifiState switch
        {
            VoWifiState.ResolvingEpdg => DashboardPhase.ResolvingEpdg,
            VoWifiState.AuthenticatingEapAka => DashboardPhase.Authenticating,
            VoWifiState.ConnectingIkev2 => DashboardPhase.BuildingTunnel,
            VoWifiState.IpsecTunnelEstablished => DashboardPhase.BuildingTunnel,
            VoWifiState.ImsRegistering => DashboardPhase.RegisteringIms,
            VoWifiState.ImsRegistered => DashboardPhase.Connected,
            VoWifiState.Failed => DashboardPhase.Failed,
            VoWifiState.Disconnected => userStopped ? DashboardPhase.Stopped : DashboardPhase.WaitingSim,
            _ => DashboardPhase.WaitingSim
        };
    }

    public static Presentation Present(DashboardPhase phase) => phase switch
    {
        DashboardPhase.NoCard => new Presentation(phase,
            "未检测到卡片", "请将 SIM/eSIM 卡插入读卡器或模组，插入后此处会自动更新。", "muted"),
        DashboardPhase.WaitingSim => new Presentation(phase,
            "等待 SIM 就绪", "已检测到设备，正在读取卡片信息，通常只需几秒钟。", "info"),
        DashboardPhase.ResolvingEpdg => new Presentation(phase,
            "正在解析 ePDG", "正在查找运营商的 VoWiFi 接入服务器地址。", "info"),
        DashboardPhase.Authenticating => new Presentation(phase,
            "正在进行身份鉴权", "正在与运营商网络 mutual 鉴权（EAP-AKA）。", "info"),
        DashboardPhase.BuildingTunnel => new Presentation(phase,
            "正在建立安全隧道", "鉴权通过，正在建立到运营商网络的安全通道。", "info"),
        DashboardPhase.RegisteringIms => new Presentation(phase,
            "正在注册 IMS", "隧道已建立，正在向运营商注册语音/短信服务。", "info"),
        DashboardPhase.Connected => new Presentation(phase,
            "已连接 · VoWiFi 可用", "VoWiFi 已注册成功，可以正常拨打电话和收发短信。", "success"),
        DashboardPhase.Failed => new Presentation(phase,
            "连接失败", "VoWiFi 连接未成功，请查看失败原因或重试。", "danger"),
        DashboardPhase.Stopped => new Presentation(phase,
            "已手动停止", "VoWiFi 当前处于断开状态，可随时重新连接。", "warning"),
        _ => new Presentation(phase, "处理中", "正在获取当前状态……", "muted")
    };

    /// <summary>
    /// Translates the engine's English failure category into an actionable
    /// Chinese sentence. Falls back to the raw category so nothing is hidden.
    /// </summary>
    public static string DescribeFailure(string? failureStage, string? failureCategory, string? lastError)
    {
        var reason = failureCategory switch
        {
            null or "" => null,
            "DNS/ePDG discovery" => "无法找到运营商 VoWiFi 服务器（ePDG）地址，可能是 DNS 或代理配置问题。",
            "proxy protocol rejected (SOCKS5 UDP required)" => "当前代理不支持 VoWiFi 所需的 UDP 转发，请更换为 SOCKS5 代理或直连。",
            "USIM availability/identity" => "读取卡片身份信息失败，请确认卡片已插好且未加 PIN 锁。",
            "ePDG discovery setup" => "准备 ePDG 连接时出错，请检查网络后重试。",
            "USIM AKA result unavailable" => "卡片未能完成网络鉴权挑战，可能是不支持 VoWiFi 的测试卡或卡片状态异常。",
            "ePDG/AAA identity or non-3GPP access policy" => "运营商拒绝了此卡的 VoWiFi 接入（未开通或不允许非蜂窝接入）。",
            "EAP-AKA server MAC verification" => "鉴权数据校验失败，可能是卡片或网络侧不匹配。",
            "ePDG/AAA EAP rejection" => "运营商拒绝了本次鉴权请求。",
            "IKE proposal negotiation" => "与运营商服务器的加密参数协商失败。",
            "IKE transport timeout" => "连接运营商服务器超时，请检查网络或代理。",
            "IPsec configuration payload" => "运营商下发的隧道配置无法解析。",
            "IMS security agreement" => "IMS 安全协商失败。",
            "SIP registration exchange" => "IMS 注册信令交换失败。",
            "IMS AKA authentication" => "IMS 二次鉴权失败。",
            "IMS registration/data plane" => "IMS 注册未完成。",
            "USIM modem" => "模组侧 USIM 交互失败。",
            "unclassified" => null,
            _ => null
        };

        if (reason == null && !string.IsNullOrWhiteSpace(failureCategory))
            reason = $"失败类别：{failureCategory}";

        var where = !string.IsNullOrWhiteSpace(failureStage) ? $"（发生阶段：{failureStage}）" : "";
        return reason ?? $"连接失败{where}，详见技术详情。";
    }

    /// <summary>Human label for the telephony state machine, used in the card panel.</summary>
    public static string DescribeTelephonyState(TelephonyState state) => state switch
    {
        TelephonyState.Init => "初始化",
        TelephonyState.SimReady => "SIM 就绪",
        TelephonyState.NetworkSearching => "搜索网络",
        TelephonyState.NetworkRegistered => "已注册网络",
        TelephonyState.DataConnecting => "数据连接中",
        TelephonyState.DataConnected => "数据已连接",
        TelephonyState.ImsRegistering => "IMS 注册中",
        TelephonyState.ImsRegistered => "IMS 已注册",
        TelephonyState.CallRinging => "来电振铃",
        TelephonyState.CallActive => "通话中",
        TelephonyState.CallEnded => "通话结束",
        TelephonyState.Error => "异常",
        _ => state.ToString()
    };
}

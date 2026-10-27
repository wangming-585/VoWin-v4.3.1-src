using CommunityToolkit.Mvvm.ComponentModel;
using VoSharp.Euicc.Models;

namespace VoWin.Models;

/// <summary>
/// 侧栏 eSIM Profile 列表项：包装底层 <see cref="Profile"/>，并附带按 ICCID
/// 匹配到的路由备注（国家 · 手机号），让用户一眼认出每张卡是干什么的。
/// 转发常用展示属性，模板无需改动绑定路径。
/// </summary>
public sealed class EuiccProfileItem : ObservableObject
{
    public Profile Inner { get; }

    private string? _resolvedCardName;
    /// <summary>
    /// 解析后的卡名（ICCID 路由表 CardName，如 "Voxi ···7427"）。
    /// 优先于卡内 ProfileName 显示——后者是运营商下发 profile 时写入的
    /// 通用名称（如 "Vodafone UK eSIM"），无法反映 MVNO 实际品牌。
    /// </summary>
    public string? ResolvedCardName
    {
        get => _resolvedCardName;
        set => SetProperty(ref _resolvedCardName, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>卡片标题：解析卡名优先，回退卡内 ProfileName。</summary>
    public string? DisplayName => !string.IsNullOrWhiteSpace(ResolvedCardName)
        ? ResolvedCardName
        : (!string.IsNullOrWhiteSpace(Nickname) ? Nickname : ProfileName);

    private string? _annotationText;
    /// <summary>国家 · 手机号 备注文本；查询不到时为 null（模板隐藏该行）。</summary>
    public string? AnnotationText
    {
        get => _annotationText;
        set => SetProperty(ref _annotationText, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    private string? _phoneToCopy;
    /// <summary>用于复制到剪贴板的原始手机号；无号码时为 null。</summary>
    public string? PhoneToCopy
    {
        get => _phoneToCopy;
        set => SetProperty(ref _phoneToCopy, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>是否有号码可复制（控制复制按钮显隐）。</summary>
    public bool HasPhone => !string.IsNullOrWhiteSpace(PhoneToCopy);

    private string? _statusText;
    /// <summary>卡片状态文本（如 VoWiFi 连接进度 / 复位中 / 失败原因）。</summary>
    public string? StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private bool _isBusy;
    /// <summary>是否处于忙碌（复位/连接中），用于展示进度动画。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    private int _retryCount;
    /// <summary>当前连接重试计数（用于直观展示"多次重试"进度）。</summary>
    public int RetryCount
    {
        get => _retryCount;
        set
        {
            if (SetProperty(ref _retryCount, value))
                OnPropertyChanged(nameof(RetryText));
        }
    }

    public string RetryText => _retryCount > 0 ? $"重试 {_retryCount} 次" : string.Empty;

    public EuiccProfileItem(Profile inner, string? annotationText, string? phoneToCopy = null, string? resolvedCardName = null)
    {
        Inner = inner;
        _resolvedCardName = string.IsNullOrWhiteSpace(resolvedCardName) ? null : resolvedCardName.Trim();
        AnnotationText = annotationText;
        PhoneToCopy = phoneToCopy;
        StatusText = null;
        IsBusy = false;
        RetryCount = 0;
    }

    /// <summary>统一更新卡名/备注与手机号（用于路由变化后动态刷新）。</summary>
    public void UpdateAnnotation(string? annotationText, string? phoneToCopy, string? resolvedCardName = null)
    {
        AnnotationText = annotationText;
        PhoneToCopy = phoneToCopy;
        if (!string.IsNullOrWhiteSpace(resolvedCardName))
        {
            ResolvedCardName = resolvedCardName;
        }
        OnPropertyChanged(nameof(HasPhone));
        OnPropertyChanged(nameof(DisplayName));
    }

    // ---- 转发属性（供 XAML 模板直接绑定，与底层 Profile 同名） ----
    public string? ProfileName => Inner.ProfileName;
    public string? ProviderName => Inner.ProviderName;
    public string? Nickname => Inner.Nickname;
    public string ICCID => Inner.ICCID;
    public ProfileState State => Inner.State;
    public bool IsEnabledState => Inner.IsEnabledState;
    public bool IsCurrentlyAddressable => Inner.IsCurrentlyAddressable;
}

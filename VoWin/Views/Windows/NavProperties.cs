using System.Windows;

namespace VoWin.Views.Windows;

/// <summary>
/// 左侧导航按钮的选中态附加属性。模板 Trigger 据此渲染高亮
/// （强调背景 + 蓝色图标块 + SemiBold 文字），代码后台在导航时切换。
/// </summary>
public static class NavProperties
{
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsActive",
            typeof(bool),
            typeof(NavProperties),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsActive(DependencyObject obj) => (bool)obj.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject obj, bool value) => obj.SetValue(IsActiveProperty, value);

    /// <summary>按钮显示文字（Content 位置被图标占用，文字改存此处）。</summary>
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.RegisterAttached(
            "Label",
            typeof(string),
            typeof(NavProperties),
            new PropertyMetadata(string.Empty));

    public static string GetLabel(DependencyObject obj) => (string)obj.GetValue(LabelProperty) ?? string.Empty;
    public static void SetLabel(DependencyObject obj, string value) => obj.SetValue(LabelProperty, value);
}

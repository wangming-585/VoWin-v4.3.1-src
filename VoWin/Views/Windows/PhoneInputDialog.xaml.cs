using System.Windows;

namespace VoWin.Views.Windows;

/// <summary>手动编辑/设置某张卡手机号的简单输入对话框。</summary>
public partial class PhoneInputDialog : Window
{
    public string? ResultPhone { get; private set; }

    public PhoneInputDialog(string? currentPhone = null)
    {
        InitializeComponent();
        PhoneBox.Text = currentPhone ?? string.Empty;
        Loaded += (_, _) =>
        {
            PhoneBox.Focus();
            PhoneBox.SelectAll();
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ResultPhone = null;
        DialogResult = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var text = PhoneBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "手机号不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ResultPhone = text;
        DialogResult = true;
    }
}

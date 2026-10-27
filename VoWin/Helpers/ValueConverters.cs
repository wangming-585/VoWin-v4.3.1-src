using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using VoSharp.Euicc.Models;
using VoSharp.Kernel.Health;
using VoSharp.Kernel.Pool;
using VoSharp.Telephony.Calls;
using VoSharp.Telephony.VoWifi;
using VoWin.Models;
using Wpf.Ui.Controls;

namespace VoWin.Helpers
{
    internal static class ThemeBrushes
    {
        public static event Action? ThemeResourcesRefreshed;

        public static Brush Get(string key, Color fallback) =>
            Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

        public static Brush Accent => Get("AppAccentBrush", Color.FromRgb(0x00, 0x7A, 0xFF));
        public static Brush Success => Get("AppSuccessBrush", Color.FromRgb(0x24, 0x8A, 0x3D));
        public static Brush Warning => Get("AppWarningBrush", Color.FromRgb(0xC9, 0x34, 0x00));
        public static Brush Danger => Get("AppDangerBrush", Color.FromRgb(0xD7, 0x00, 0x15));
        public static Brush Muted => Get("AppTextTertiaryBrush", Color.FromRgb(0x8E, 0x8E, 0x93));

        public static void NotifyThemeResourcesRefreshed() => ThemeResourcesRefreshed?.Invoke();
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool val = value is bool b && b;
            if (Invert) val = !val;
            return val ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isVis = value is Visibility v && v == Visibility.Visible;
            return Invert ? !isVis : isVis;
        }
    }

    public class NullToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isNull = value == null || (value is string s && string.IsNullOrEmpty(s));
            if (Invert) isNull = !isNull;
            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class CountToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int count = 0;
            if (value is int i) count = i;
            else if (value is System.Collections.ICollection c) count = c.Count;

            bool isEmpty = count == 0;
            if (Invert) isEmpty = !isEmpty;
            return isEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class SmsDirectionToAlignmentConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isOutgoing && isOutgoing)
                return HorizontalAlignment.Right;
            return HorizontalAlignment.Left;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class SmsDirectionToMarginConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isOutgoing && isOutgoing)
                return new Thickness(80, 4, 12, 4);
            return new Thickness(12, 4, 80, 4);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class SmsDirectionToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isOutgoing = false;
            if (value is bool b) isOutgoing = b;
            else if (value != null && value.ToString() == "Outgoing") isOutgoing = true;

            if (isOutgoing)
            {
                return ThemeBrushes.Accent;
            }

            // In Dark Mode, resolve to dark card background; in Light Mode, resolve to light card background
            return Application.Current?.TryFindResource("AppCardBrush") as Brush
                ?? Application.Current?.TryFindResource("ControlFillColorDefaultBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class SmsDirectionToForegroundConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool isOutgoing = false;
            if (value is bool b) isOutgoing = b;
            else if (value != null && value.ToString() == "Outgoing") isOutgoing = true;

            if (isOutgoing)
            {
                return Brushes.White;
            }

            // In Dark Mode, resolve to light/white text; in Light Mode, resolve to dark text
            return Application.Current?.TryFindResource("AppTextPrimaryBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class CallDirectionToSymbolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CallDirection dir)
            {
                return dir == CallDirection.Incoming ? SymbolRegular.CallInbound24 : SymbolRegular.CallOutbound24;
            }
            return SymbolRegular.Call24;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class CallDirectionToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CallDirection dir)
            {
                return dir == CallDirection.Incoming
                    ? ThemeBrushes.Accent
                    : ThemeBrushes.Success;
            }
            return ThemeBrushes.Muted;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class CallDirectionToStringConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CallDirection dir)
            {
                return dir == CallDirection.Incoming ? "呼入" : "呼出";
            }
            return "未知";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class CallStateToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CallState state)
            {
                return state switch
                {
                    CallState.Active => ThemeBrushes.Success,
                    CallState.Dialing or CallState.Ringing or CallState.Incoming => ThemeBrushes.Accent,
                    CallState.Held => ThemeBrushes.Warning,
                    CallState.Ended => ThemeBrushes.Danger,
                    _ => ThemeBrushes.Muted
                };
            }
            return ThemeBrushes.Muted;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class VoWifiStateToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is VoWifiState state)
            {
                return state switch
                {
                    VoWifiState.ImsRegistered => ThemeBrushes.Success,
                    VoWifiState.IpsecTunnelEstablished or VoWifiState.ImsRegistering => ThemeBrushes.Accent,
                    VoWifiState.ResolvingEpdg or VoWifiState.ConnectingIkev2 or VoWifiState.AuthenticatingEapAka => ThemeBrushes.Warning,
                    VoWifiState.Failed => ThemeBrushes.Danger,
                    _ => ThemeBrushes.Muted
                };
            }
            return ThemeBrushes.Muted;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class SlotStateToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is SlotState state)
            {
                return state switch
                {
                    SlotState.Online => ThemeBrushes.Success,
                    SlotState.Busy => ThemeBrushes.Warning,
                    SlotState.Error => ThemeBrushes.Danger,
                    _ => ThemeBrushes.Muted
                };
            }
            return ThemeBrushes.Muted;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// Maps a watchdog health verdict (or SlotHealth snapshot) to a color:
    /// Healthy green / Stale yellow / Degraded orange / Failed red / Suppressed gray.
    /// </summary>
    public class SlotHealthToBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var verdict = value switch
            {
                HealthVerdict v => v,
                SlotHealth h => h.Verdict,
                _ => (HealthVerdict?)null
            };
            return verdict switch
            {
                HealthVerdict.Healthy => ThemeBrushes.Success,
                HealthVerdict.Stale => ThemeBrushes.Get("AppStaleBrush", Color.FromRgb(0xD4, 0xA0, 0x17)),
                HealthVerdict.Degraded => ThemeBrushes.Warning,
                HealthVerdict.Failed => ThemeBrushes.Danger,
                _ => ThemeBrushes.Muted
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>Chinese label for a watchdog health verdict (or SlotHealth snapshot).</summary>
    public class SlotHealthToTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var verdict = value switch
            {
                HealthVerdict v => v,
                SlotHealth h => h.Verdict,
                _ => (HealthVerdict?)null
            };
            return verdict switch
            {
                HealthVerdict.Healthy => "看门狗: 健康",
                HealthVerdict.Stale => "看门狗: 数据滞后",
                HealthVerdict.Degraded => "看门狗: 降级",
                HealthVerdict.Failed => "看门狗: 故障",
                HealthVerdict.Suppressed => "看门狗: 熔断中",
                _ => "看门狗: 未评估"
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class ProfileStateToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is ProfileState state)
            {
                return state switch
                {
                    ProfileState.Enabled => ThemeBrushes.Success,
                    _ => ThemeBrushes.Muted
                };
            }
            return ThemeBrushes.Muted;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class SignalBarsToSymbolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int bars = value is int b ? b : 0;
            return bars switch
            {
                >= 4 => SymbolRegular.CellularData524,
                3 => SymbolRegular.CellularData424,
                2 => SymbolRegular.CellularData324,
                1 => SymbolRegular.CellularData224,
                _ => SymbolRegular.CellularOff24
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToSuccessDangerBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool ok = value is bool b && b;
            return ok ? ThemeBrushes.Success : ThemeBrushes.Danger;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToSuccessDangerBackgroundConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool ok = value is bool b && b;
            return ok
                ? ThemeBrushes.Get("AppSuccessSoftBrush", Color.FromRgb(0xE7, 0xF8, 0xF2))
                : ThemeBrushes.Get("AppDangerSoftBrush", Color.FromRgb(0xFF, 0xF0, 0xF3));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToSuccessDangerSymbolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool ok = value is bool b && b;
            return ok ? SymbolRegular.Checkmark24 : SymbolRegular.Dismiss24;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class CallRecordToSymbolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CallRecordModel record)
            {
                if (record.Direction == CallDirection.Missed)
                    return SymbolRegular.CallDismiss24;
                if (record.Duration == TimeSpan.Zero && record.Direction == CallDirection.Outgoing)
                    return SymbolRegular.Warning24;
                return record.Direction == CallDirection.Incoming
                    ? SymbolRegular.CallInbound24
                    : SymbolRegular.CallOutbound24;
            }
            return SymbolRegular.Call24;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class CallRecordToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is CallRecordModel record)
            {
                if (record.Direction == CallDirection.Missed)
                    return ThemeBrushes.Danger;
                if (record.Duration == TimeSpan.Zero && record.Direction == CallDirection.Outgoing)
                    return ThemeBrushes.Warning;
                return record.Direction == CallDirection.Incoming
                    ? ThemeBrushes.Accent
                    : ThemeBrushes.Success;
            }
            return ThemeBrushes.Muted;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

/// <summary>
/// Returns true when the bound int is greater than or equal to ConverterParameter.
/// Used by the dashboard progress dots to light up completed connection steps.
/// </summary>
public class IntGreaterOrEqualConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int threshold = parameter is string s && int.TryParse(s, out var parsed) ? parsed : 0;
        return value is int current && current >= threshold;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}


/// <summary>
/// Masks a sensitive identifier (ICCID/IMSI/EID) for default display, e.g.
/// "8986007311325300026" becomes "8986 ···0026". Keep the ToolTip bound to the
/// raw value so the full identifier stays one hover away, per the UX brief.
/// </summary>
public class MaskedIdentifierConverter : IValueConverter
{
    public int PrefixLength { get; set; } = 4;
    public int SuffixLength { get; set; } = 4;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s) || s == "—" || s == "未知")
            return s ?? string.Empty;

        var prefix = Math.Min(PrefixLength, s.Length);
        var suffix = Math.Min(SuffixLength, Math.Max(0, s.Length - prefix));
        if (s.Length <= PrefixLength + SuffixLength)
            return s; // too short to mask meaningfully
        return $"{s[..prefix]} ···{s.Substring(s.Length - suffix)}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

}
}

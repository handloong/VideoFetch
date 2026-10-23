using System.Globalization;
using System.Windows.Data;
using VideoFetch.Models;

namespace VideoFetch.Converters;

/// <summary>
/// Converts (value, maximum, totalWidth) → pixel width for the ProgressBar indicator
/// </summary>
public class ProgressToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return 0.0;
        if (!double.TryParse(values[0]?.ToString(), out var val)) return 0.0;
        if (!double.TryParse(values[1]?.ToString(), out var max) || max <= 0) return 0.0;
        if (!double.TryParse(values[2]?.ToString(), out var width)) return 0.0;
        return Math.Max(0, Math.Min(width, val / max * width));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Returns true if the value is not null and not empty string
/// </summary>
public class NotNullOrEmptyConverter : IValueConverter
{
    /// <summary>
    /// 非空 → true；绑定目标是 Visibility 时直接给 Visible/Collapsed。
    ///
    /// 之前一律返回 bool，而所有调用点都是绑在 Visibility 上：bool 到 Visibility
    /// 没有默认转换，绑定静默失败、Visibility 保持默认的 Visible —— 于是"没有状态
    /// 消息"的 TextBlock 也一直占着布局位置。
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasValue = value is string s ? !string.IsNullOrEmpty(s) : value != null;

        if (targetType == typeof(System.Windows.Visibility))
            return hasValue ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        return hasValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// BoolToVisibility converter (true=Visible, false=Collapsed by default)
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; } = false;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool bv && bv;
        if (Invert) b = !b;
        return b ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Returns Visibility.Visible if int value > 0, otherwise Collapsed.
/// Set ConverterParameter=Invert to reverse.
/// Useful for ObservableCollection.Count bindings.
/// </summary>
public class IntGreaterThanZeroConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool visible = false;
        if (int.TryParse(value?.ToString(), out var i))
            visible = i > 0;

        if (parameter?.ToString()?.Equals("Invert", StringComparison.OrdinalIgnoreCase) == true)
            visible = !visible;

        return visible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Two-way enum ↔ bool converter for RadioButton groups:
/// IsChecked="{Binding SomeEnum, Converter={StaticResource EnumEquals},
///             ConverterParameter={x:Static models:SomeEnum.Value}}"
/// </summary>
public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null && value.Equals(parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b) return parameter!;
        return System.Windows.DependencyProperty.UnsetValue;
    }
}

/// <summary>
/// 按 <see cref="DownloadState"/> 给队列的状态文字上色。
///
/// 以前是拿字符串比对（还要 StartsWith("Failed") 兜住 "Failed: xxx"），
/// 中文界面里根本匹配不上；现在只认枚举。
/// </summary>
public class StateToColorConverter : IValueConverter
{
    private static readonly System.Windows.Media.SolidColorBrush Success = Frozen(0x4C, 0xAF, 0x50);
    private static readonly System.Windows.Media.SolidColorBrush Danger = Frozen(0xF4, 0x43, 0x36);
    private static readonly System.Windows.Media.SolidColorBrush Accent = Frozen(0x7C, 0x6F, 0xF7);
    private static readonly System.Windows.Media.SolidColorBrush Muted = Frozen(0x99, 0x99, 0xAA);
    private static readonly System.Windows.Media.SolidColorBrush Warning = Frozen(0xFF, 0x98, 0x00);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            DownloadState.Completed => Success,
            DownloadState.Failed => Danger,
            DownloadState.Downloading => Accent,
            DownloadState.Queued => Muted,
            DownloadState.Cancelled => Muted,
            DownloadState.Deleted => Muted,
            DownloadState.FileMissing => Muted,
            _ => Warning
        };

    /// <summary>画刷冻结后可以跨线程共享，也免掉每次刷新都 new 一个。</summary>
    private static System.Windows.Media.SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

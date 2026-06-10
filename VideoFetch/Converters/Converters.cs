using System.Globalization;
using System.Windows.Data;

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
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s ? !string.IsNullOrEmpty(s) : value != null;

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
/// Returns a solid color brush based on a DownloadItem status string
/// </summary>
public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value?.ToString() ?? "";
        return status switch
        {
            "Completed" => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)),
            var s when s.StartsWith("Failed") => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36)),
            "Downloading" => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x7C, 0x6F, 0xF7)),
            "Queued" => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x99, 0x99, 0xAA)),
            _ => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0x98, 0x00))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

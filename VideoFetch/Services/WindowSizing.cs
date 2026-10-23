using System;

namespace VideoFetch.Services;

/// <summary>
/// 主窗口默认尺寸：黄金比（1 : 0.618）。
///
/// 直接写死在 XAML 里会在小屏笔记本上把标题栏顶到屏幕外，所以默认值放在这里，
/// 启动时按"当前屏幕可用工作区"再算一次 —— 装不下就等比缩小，比例不变。
/// </summary>
public static class WindowSizing
{
    /// <summary>黄金比（长 : 宽 = 1.618 : 1）。</summary>
    public const double GoldenRatio = 1.618;

    /// <summary>默认宽度（高度由此推出 ≈ 791）。</summary>
    public const double DefaultWidth = 1280;

    /// <summary>最多占工作区的比例，留点边给任务栏和窗口阴影。</summary>
    public const double MaxWorkAreaUsage = 0.92;

    public static double GoldenHeight(double width) => Math.Round(width / GoldenRatio);

    /// <summary>
    /// 按黄金比给出窗口尺寸，并保证不超过屏幕可用区域。
    /// </summary>
    /// <param name="preferredWidth">期望宽度</param>
    /// <param name="workAreaWidth">系统工作区宽度（DIP）</param>
    /// <param name="workAreaHeight">系统工作区高度（DIP）</param>
    /// <param name="minWidth">窗口最小宽度，缩放时不会低于它（除非屏幕比它还小）</param>
    public static (double Width, double Height) Fit(
        double preferredWidth, double workAreaWidth, double workAreaHeight, double minWidth)
    {
        if (preferredWidth <= 0) preferredWidth = DefaultWidth;
        if (workAreaWidth <= 0 || workAreaHeight <= 0)
            return (preferredWidth, GoldenHeight(preferredWidth));

        var width = preferredWidth;
        var height = GoldenHeight(width);

        var maxWidth = workAreaWidth * MaxWorkAreaUsage;
        var maxHeight = workAreaHeight * MaxWorkAreaUsage;

        if (width > maxWidth)
        {
            width = maxWidth;
            height = GoldenHeight(width);
        }

        if (height > maxHeight)
        {
            height = maxHeight;
            width = Math.Round(height * GoldenRatio);
        }

        // 只有屏幕足够大时才守最小宽度，否则窗口会比屏幕还宽
        if (minWidth > 0 && width < minWidth && maxWidth >= minWidth)
        {
            width = minWidth;
            height = GoldenHeight(width);
        }

        return (Math.Round(width), Math.Round(height));
    }
}

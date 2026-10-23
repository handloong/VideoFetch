using System;
using VideoFetch.Services;

namespace VideoFetch.Test;

/// <summary>
/// 主窗口默认尺寸的黄金比与"装不下就等比缩"。
/// </summary>
internal static class WindowSizingTests
{
    private static int _failed;

    public static bool Run()
    {
        Console.WriteLine("\n[window-sizing]");
        var before = _failed;

        Safe("golden", GoldenRatioOnBigScreen);
        Safe("small-screen", ShrinksOnSmallScreen);
        Safe("min-width", RespectsMinWidth);
        Safe("no-workarea", FallsBackWithoutWorkArea);

        return _failed == before;
    }

    private static void GoldenRatioOnBigScreen()
    {
        // 2560x1440（本机）：用默认尺寸，长宽比 1.618
        var (w, h) = WindowSizing.Fit(WindowSizing.DefaultWidth, 2560, 1400, 960);

        Check("window: 默认宽度 1280", w == 1280, w.ToString());
        Check("window: 高度 791", h == 791, h.ToString());
        Check("window: 长宽比是黄金比", Math.Abs(w / h - 1.618) < 0.005, (w / h).ToString("0.000"));
        Check("window: 比原来的 1000x700 更大", w > 1000 && h > 700);
    }

    private static void ShrinksOnSmallScreen()
    {
        // 1366x768 笔记本，任务栏后剩 728 高
        var (w, h) = WindowSizing.Fit(WindowSizing.DefaultWidth, 1366, 728, 960);

        Check("window: 小屏不超出工作区高度", h <= 728 * 0.92 + 0.5, h.ToString());
        Check("window: 小屏不超出工作区宽度", w <= 1366 * 0.92 + 0.5, w.ToString());
        Check("window: 缩小后仍保持黄金比", Math.Abs(w / h - 1.618) < 0.01, (w / h).ToString("0.000"));
    }

    private static void RespectsMinWidth()
    {
        // 大屏但用户手动要求窄一点：不能低于最小宽度（960）
        var (w, _) = WindowSizing.Fit(800, 1920, 1040, 960);
        Check("window: 不低于最小宽度", w >= 960, w.ToString());
    }

    private static void FallsBackWithoutWorkArea()
    {
        var (w, h) = WindowSizing.Fit(WindowSizing.DefaultWidth, 0, 0, 960);
        Check("window: 拿不到工作区时用默认尺寸", w == 1280 && h == 791, $"{w}x{h}");
    }

    private static void Safe(string name, Action test)
    {
        try { test(); }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}: 抛异常 {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
    }

    private static void Check(string name, bool condition, string? actual = null)
    {
        Console.WriteLine(condition
            ? $"  PASS  {name}"
            : $"  FAIL  {name}" + (actual is null ? "" : $"  (实际: {actual})"));
        if (!condition) _failed++;
    }
}

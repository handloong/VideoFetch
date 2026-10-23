using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VideoFetch.ViewModels;
using VideoFetch.Views;

namespace VideoFetch.Test;

/// <summary>
/// UI 巡检：把每个页面在几种窗口尺寸下离屏渲染，然后遍历视觉树找出
/// "肉眼能看出、但没有断言盯着"的问题。
///
/// 检查项（都是这次几个 bug 的同类）：
///   1. 文字被裁切  —— TextBlock 没设省略号却放不下（"标题太长看不全"）
///   2. 控件尺寸为 0 —— 有内容却 0 宽/0 高（滚动条被压成 6px 那种）
///   3. 越出窗口     —— 元素几何跑到窗口外（不在滚动容器里的才算）
///   4. 兄弟重叠     —— StackPanel 里两个子元素叠在一起
///   5. 对比度不足   —— 文字前景 vs 实际背景，WCAG 比值 < 3
///
/// 只报告不抛异常：这里的原则是"先看见"，稳下来之后再挑几条变成硬断言。
/// </summary>
internal static class UiAudit
{
    private static readonly (string Name, int Width, int Height)[] Sizes =
    {
        ("最小 880x580", 880, 580),
        ("默认 1000x700", 1000, 700),
        ("大屏 1400x900", 1400, 900)
    };

    private static readonly List<string> Findings = new();

    public static int Run(MainViewModel vm)
    {
        Console.WriteLine("\n[ui-audit]");
        Findings.Clear();

        foreach (var size in Sizes)
        {
            AuditTab("搜索页", new SearchTab { DataContext = vm }, size);
            AuditTab("分类页", new CategoryTab { DataContext = vm }, size);
            AuditTab("下载页", new QueueTab { DataContext = vm }, size);
            AuditTab("设置页", new SettingsTab { DataContext = vm }, size);
        }

        // 同一个问题会在三种尺寸下重复报，按文案去重
        var unique = Findings.Distinct().ToList();

        if (unique.Count == 0)
        {
            Console.WriteLine("  OK  多尺寸下没有发现布局问题");
            return 0;
        }

        foreach (var line in unique)
            Console.WriteLine("  WARN  " + line);

        Console.WriteLine($"  合计 {unique.Count} 类问题");
        return unique.Count;
    }

    private static void AuditTab(string name, FrameworkElement tab,
        (string Name, int Width, int Height) size)
    {
        tab.Measure(new Size(size.Width, size.Height));
        tab.Arrange(new Rect(0, 0, size.Width, size.Height));
        tab.UpdateLayout();

        var where = $"[{name}@{size.Name}]";
        AuditElement(tab, tab, size, where);
        AuditSiblingOverlap(tab, where);
    }

    private static void AuditElement(DependencyObject node, FrameworkElement tab,
        (string Name, int Width, int Height) size, string where)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is FrameworkElement element && IsShown(element))
            {
                CheckClippedText(element, where);
                CheckZeroSize(element, where);
                CheckOutOfBounds(element, tab, size, where);
                CheckContrast(element, where);
            }

            AuditElement(child, tab, size, where);
        }
    }

    // ── 1. 文字放不下又没有省略号 ──────────────────────────────
    private static void CheckClippedText(FrameworkElement element, string where)
    {
        if (element is not TextBlock { Text.Length: > 0 } text) return;
        if (text.TextTrimming != TextTrimming.None) return;
        if (text.TextWrapping != TextWrapping.NoWrap) return;
        if (text.ActualWidth <= 0) return;

        // DesiredSize 含 Margin、ActualWidth 不含 —— 不减掉就会误报
        // （"需要 153px 只有 147px" 其实就是 6px 的左边距）
        var needed = text.DesiredSize.Width - text.Margin.Left - text.Margin.Right;
        if (needed > text.ActualWidth + 1)
            Report($"{where} 文字被裁切：\"{Short(text.Text)}\" 需要 {needed:0}px，只有 {text.ActualWidth:0}px");
    }

    // ── 2. 有内容却量出 0 尺寸 ─────────────────────────────────
    private static void CheckZeroSize(FrameworkElement element, string where)
    {
        var hasContent = element switch
        {
            Button b => b.Content != null,
            CheckBox c => c.Content != null,
            TextBlock t => t.Text.Length > 0,
            ComboBox c => c.Items.Count > 0,
            ProgressBar => true,
            _ => false
        };
        if (!hasContent) return;

        if (element.ActualWidth < 1)
            Report($"{where} {element.GetType().Name} 宽度为 0：\"{Describe(element)}\"");
        else if (element.ActualHeight < 1)
            Report($"{where} {element.GetType().Name} 高度为 0：\"{Describe(element)}\"");
    }

    // ── 3. 跑出窗口（滚动容器里的内容不算） ────────────────────
    private static void CheckOutOfBounds(FrameworkElement element, FrameworkElement tab,
        (string Name, int Width, int Height) size, string where)
    {
        if (HasScrollAncestor(element, tab)) return;
        if (element is ScrollBar or Thumb) return;

        Rect bounds;
        try
        {
            bounds = element.TransformToAncestor(tab).TransformBounds(new Rect(element.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return; // 不在同一棵树里
        }

        const double slack = 1.5;
        if (bounds.Right > size.Width + slack || bounds.Bottom > size.Height + slack ||
            bounds.Left < -slack || bounds.Top < -slack)
        {
            Report($"{where} 越出窗口：{element.GetType().Name} \"{Describe(element)}\" " +
                   $"({bounds.Left:0},{bounds.Top:0})-({bounds.Right:0},{bounds.Bottom:0})");
        }
    }

    // ── 4. StackPanel 子元素重叠 ───────────────────────────────
    private static void AuditSiblingOverlap(DependencyObject node, string where)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is StackPanel panel && IsShown(panel))
            {
                CheckOverlap(panel, where);
                CheckPanelOverflow(panel, where);
            }
            AuditSiblingOverlap(child, where);
        }
    }

    private static void CheckPanelOverflow(StackPanel panel, string where)
    {
        var children = panel.Children.OfType<FrameworkElement>()
            .Where(c => IsShown(c) && c.Visibility == Visibility.Visible)
            .ToList();
        if (children.Count == 0) return;

        var total = panel.Orientation == Orientation.Horizontal
            ? children.Sum(c => c.DesiredSize.Width)
            : children.Sum(c => c.DesiredSize.Height);
        var available = panel.Orientation == Orientation.Horizontal ? panel.ActualWidth : panel.ActualHeight;

        if (available > 0 && total > available + 1)
            Report($"{where} 内容超出容器 {total - available:0}px" +
                   $"（{panel.Orientation} StackPanel 需要 {total:0}，只有 {available:0}）");
    }

    private static void CheckOverlap(StackPanel panel, string where)
    {
        var children = panel.Children.OfType<FrameworkElement>()
            .Where(c => IsShown(c) && c.ActualWidth > 1 && c.ActualHeight > 1)
            .ToList();

        for (var i = 0; i < children.Count; i++)
        for (var j = i + 1; j < children.Count; j++)
        {
            var a = new Rect(0, 0, children[i].ActualWidth, children[i].ActualHeight);
            var b = new Rect(0, 0, children[j].ActualWidth, children[j].ActualHeight);
            var pa = children[i].TranslatePoint(new Point(0, 0), panel); a.Offset(pa.X, pa.Y);
            var pb = children[j].TranslatePoint(new Point(0, 0), panel); b.Offset(pb.X, pb.Y);

            var hit = Rect.Intersect(a, b);
            if (hit.IsEmpty || hit.Width * hit.Height < 4) continue;

            Report($"{where} 元素重叠：\"{Describe(children[i])}\" 与 \"{Describe(children[j])}\" " +
                   $"重叠 {hit.Width:0}x{hit.Height:0}px");
        }
    }

    // ── 5. 文字对比度 ─────────────────────────────────────────
    private static void CheckContrast(FrameworkElement element, string where)
    {
        if (element is not TextBlock { Text.Length: > 0 } text || !IsShown(text)) return;
        if (text.Foreground is not SolidColorBrush fg) return;

        var bg = ResolveBackground(text);
        var ratio = ContrastRatio(fg.Color, bg);
        if (ratio < 3.0)
            Report($"{where} 对比度不足 {ratio:0.0}:1：\"{Short(text.Text)}\" " +
                   $"前景 {fg.Color} / 背景 {bg}");
    }

    private static Color ResolveBackground(DependencyObject start)
    {
        for (var node = start; node != null; node = VisualTreeHelper.GetParent(node))
        {
            // 只认真正会被绘制出来的背景。CheckBox/RadioButton 这类控件的
            // Control.Background 默认是系统白，但它们并不画它 —— 认了就会误报
            // "白底白字"。Button 是真的会把 Background 画出来。
            var brush = node switch
            {
                Panel p => p.Background,
                Border b => b.Background,
                Button btn => btn.Background,
                _ => null
            };

            if (brush is SolidColorBrush { Color.A: > 0 } solid)
                return solid.Color;
        }

        // 一路透明 → 用主题底色
        return (Application.Current?.TryFindResource("BgPrimaryBrush") as SolidColorBrush)?.Color
               ?? Color.FromRgb(0x1E, 0x1E, 0x2E);
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static bool HasScrollAncestor(DependencyObject node, DependencyObject stop)
    {
        for (var parent = VisualTreeHelper.GetParent(node); parent != null && parent != stop;
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer) return true;
        }
        return false;
    }

    private static void Report(string message) => Findings.Add(message);

    /// <summary>
    /// 离屏布局（Measure/Arrange 但不 Show）时 UIElement.IsVisible 恒为 false，
    /// 必须顺着 VisualTree 看 Visibility 才知道"用户眼里它出不出现"。
    /// </summary>
    private static bool IsShown(DependencyObject node)
    {
        for (var current = node; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
    }

    private static string Describe(FrameworkElement element) => element switch
    {
        TextBlock t => Short(t.Text),
        Button { Content: string s } => Short(s),
        CheckBox { Content: string s } => Short(s),
        _ => element.Name is { Length: > 0 } n ? n : element.GetType().Name
    };

    private static string Short(string text)
        => text.Length <= 24 ? text : text[..24] + "…";
}

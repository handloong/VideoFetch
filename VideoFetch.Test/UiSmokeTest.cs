using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoFetch.Models;
using VideoFetch.ViewModels;
using VideoFetch.Views;

namespace VideoFetch.Test;

/// <summary>
/// Offscreen render smoke test for the category tab.
///
/// Real sites expose 12 (91pinse) to 105 (PornHub) categories. This test builds
/// the tab with 105 fake entries, lays it out at 900×520 and asserts:
///   1. every StaticResource used by CategoryTab.xaml still resolves,
///   2. the tag cloud stays inside its 112px band instead of pushing the
///      results grid out of the window (the clipping bug),
///   3. the rendered bitmap is non-blank (saved to ui-smoke* for inspection).
/// </summary>
internal static class UiSmokeTest
{
    private const double TabWidth = 900;
    private const double TabHeight = 520;
    private const double MaxTagCloudHeight = 112;
    private const int FakeCategoryCount = 105;

    public static bool Run()
    {
        var ok = false;
        var thread = new Thread(() =>
        {
            try { ok = RunOnSta(); }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL  ui-smoke: {ex.GetType().Name}: {ex.Message}");
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return ok;
    }

    private static bool RunOnSta()
    {
        Console.WriteLine("\n[ui-smoke]");

        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/VideoFetch;component/Themes/DarkTheme.xaml")
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/VideoFetch;component/Strings/Strings.zh-CN.xaml")
        });

        var vm = new MainViewModel();
        for (var i = 1; i <= FakeCategoryCount; i++)
            vm.Categories.Add(new CategoryItem
            {
                Name = $"分类标签 {i}",
                Key = $"/video?c={i}",
                Site = SiteType.PornHub
            });

        // A few results so the results list (not the empty state) is laid out too
        for (var i = 1; i <= 3; i++)
            vm.CategoryResults.Add(new SearchResult
            {
                Title = $"示例视频 {i}",
                Url = $"https://www.pornhub.com/view_video.php?viewkey={i}",
                Duration = "10:00",
                Site = SiteType.PornHub
            });

        var tab = new CategoryTab { DataContext = vm };
        tab.Measure(new Size(TabWidth, TabHeight));
        tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
        tab.UpdateLayout();

        var ok = true;

        // 1) resource resolution — a missing StaticResource throws at load time,
        //    so reaching here already covered it; assert the tag list bound.
        var tagList = FindDescendant<ListBox>(tab, lb => ReferenceEquals(lb.ItemsSource, vm.Categories));
        Check("ui-smoke: tag cloud bound to Categories", tagList != null);
        if (tagList == null) return false;

        // 2) the cloud must not exceed its scroll band
        var height = tagList.ActualHeight;
        Console.WriteLine($"  info  tag cloud height = {height:0.#}px (max {MaxTagCloudHeight})");
        Check("ui-smoke: tag cloud stays within 112px", height <= MaxTagCloudHeight + 0.5);
        Check("ui-smoke: tag cloud scrolls instead of clipping",
            height > 0 && ScrollViewer.GetVerticalScrollBarVisibility(tagList) == ScrollBarVisibility.Auto);

        // 3) results area must still have room
        var results = FindDescendant<ListView>(tab, lv => lv.Name == "CategoryResultsList");
        Check("ui-smoke: results list laid out", results != null && results.ActualHeight > 100);
        if (results != null)
            Console.WriteLine($"  info  results height = {results.ActualHeight:0.#}px");

        // 4) render, and make sure we actually drew something
        var bmp = new RenderTargetBitmap((int)TabWidth, (int)TabHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(tab);
        var path = Path.Combine(AppContext.BaseDirectory, "ui-smoke-categorytab.png");
        using (var fs = File.Create(path))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(fs);
        }
        Check("ui-smoke: rendered bitmap is not blank", !IsBlank(bmp));
        Console.WriteLine($"  info  screenshot: {path}");

        // 5) 下载页：失败行必须给出「重试 / 删除任务」，不能再是点了没反应的「取消」
        ok &= CheckQueueTab(vm);

        // 6) 搜索结果 / 分类结果的行右键菜单
        ok &= CheckResultContextMenus(vm);

        // 7) 时长筛选下拉框（仅 91pinse）
        ok &= CheckDurationFilter(vm);

        // 8) 弹层主题（右键菜单 / ToolTip 都是独立视觉树，不继承主题）
        ok &= CheckPopupThemes();

        return ok;
    }

    /// <summary>
    /// 右键菜单和 ToolTip 都是独立弹层：拿不到主题资源，WPF 默认给的是系统
    /// 浅色底，而全局 TextBlock 样式把文字强制成近白色 —— 出现过"浅底白字
    /// 完全看不清"。这里渲染真实像素来判断，比读属性可靠（模板里的画刷
    /// 不一定体现在控件属性上）。
    /// </summary>
    private static bool CheckPopupThemes()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = Application.Current.TryFindResource("Menu_OpenSourceUrl") as string ?? "menu"
        });

        var tip = new ToolTip
        {
            Content = "Failed: Response status code does not indicate success: 410 (Gone)."
        };

        var ok = CheckPopup("右键菜单", menu, "ui-smoke-contextmenu.png");
        ok &= CheckPopup("ToolTip", tip, "ui-smoke-tooltip.png");
        return ok;
    }

    /// <summary>渲染一个弹层，从像素里找出底色和文字色并校验对比度。</summary>
    private static bool CheckPopup(string label, FrameworkElement popup, string fileName)
    {
        popup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = popup.DesiredSize;
        if (!Check($"主题: {label} 有内容可渲染", size.Width > 0 && size.Height > 0)) return false;

        popup.Arrange(new Rect(0, 0, size.Width, size.Height));
        popup.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height),
            96, 96, PixelFormats.Pbgra32);
        bmp.Render(popup);

        var (bg, fg) = SampleColors(bmp);
        var ratio = ContrastRatio(bg, fg);
        Console.WriteLine($"  info  {label}: 底 #{bg.R:X2}{bg.G:X2}{bg.B:X2}  " +
                          $"字 #{fg.R:X2}{fg.G:X2}{fg.B:X2}  对比度 {ratio:0.0}:1  " +
                          $"尺寸 {size.Width:0}x{size.Height:0}");

        var ok = Check($"主题: {label} 底色是暗色", Luminance(bg) < 0.15);
        ok &= Check($"主题: {label} 文字够亮（对比度 >= 4.5:1）", ratio >= 4.5);

        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        using (var fs = File.Create(path))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(fs);
        }
        Console.WriteLine($"  info  screenshot: {path}");

        return ok;
    }

    /// <summary>
    /// 从渲染结果里估底色（出现最多的颜色）和文字色（出现 3 次以上最亮的颜色）。
    /// </summary>
    private static (Color Background, Color Foreground) SampleColors(BitmapSource bmp)
    {
        var stride = bmp.PixelWidth * 4;
        var pixels = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(pixels, stride, 0);

        var counts = new Dictionary<uint, int>();
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var key = (uint)((pixels[i + 2] << 16) | (pixels[i + 1] << 8) | pixels[i]);
            counts.TryGetValue(key, out var n);
            counts[key] = n + 1;
        }

        var bgKey = 0u;
        var bgCount = -1;
        foreach (var kv in counts)
            if (kv.Value > bgCount) { bgCount = kv.Value; bgKey = kv.Key; }

        var fgKey = bgKey;
        var fgLum = Luminance(ToColor(bgKey));
        foreach (var kv in counts)
        {
            if (kv.Value < 3) continue;              // 跳过抗锯齿边缘
            var lum = Luminance(ToColor(kv.Key));
            if (lum > fgLum) { fgLum = lum; fgKey = kv.Key; }
        }

        return (ToColor(bgKey), ToColor(fgKey));
    }

    private static Color ToColor(uint rgb)
        => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ── WCAG 相对亮度 / 对比度 ──
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var l1 = Luminance(a);
        var l2 = Luminance(b);
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    /// <summary>
    /// 时长筛选下拉框：只在支持的站点（91pinse）出现，且选择能双向绑定到 ViewModel。
    /// 放在最后跑是因为切站点会清空分类结果，而前面的断言还要用到它们。
    /// </summary>
    private static bool CheckDurationFilter(MainViewModel vm)
    {
        var ok = true;

        vm.SearchSite = SiteType.PornHub;
        ok &= Check("ui-smoke: PornHub 搜索页不显示时长筛选", !vm.ShowSearchDurationFilter);
        vm.SearchSite = SiteType.PinSe;
        ok &= Check("ui-smoke: 91pinse 搜索页显示时长筛选", vm.ShowSearchDurationFilter);

        ok &= Check("ui-smoke: 时长选项 4 项", vm.DurationOptions.Count == 4);
        ok &= Check("ui-smoke: 时长文案来自语言资源",
            vm.DurationOptions[0].Label == (Application.Current.TryFindResource("Dur_All") as string));

        var tab = new SearchTab { DataContext = vm };
        tab.Measure(new Size(TabWidth, TabHeight));
        tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
        tab.UpdateLayout();

        var combo = FindDescendant<ComboBox>(tab, c => ReferenceEquals(c.ItemsSource, vm.DurationOptions));
        ok &= Check("ui-smoke: 时长下拉框已绑定", combo != null);
        if (combo == null) return false;

        ok &= Check("ui-smoke: 时长下拉框对 91pinse 可见", IsEffectivelyVisible(combo));
        ok &= Check("ui-smoke: 默认全部时长", Equals(combo.SelectedValue, DurationFilter.All));

        // 截图留档（在改动筛选值之前拍，反映默认状态）
        var bmp = new RenderTargetBitmap((int)TabWidth, (int)TabHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(tab);
        var path = Path.Combine(AppContext.BaseDirectory, "ui-smoke-searchtab.png");
        using (var fs = File.Create(path))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(fs);
        }
        ok &= Check("ui-smoke: 搜索页截图非空白", !IsBlank(bmp));
        Console.WriteLine($"  info  screenshot: {path}");

        combo.SelectedValue = DurationFilter.Long;
        combo.UpdateLayout();
        ok &= Check("ui-smoke: 选择 20+ 分钟同步到 ViewModel", vm.DurationFilter == DurationFilter.Long);

        vm.DurationFilter = DurationFilter.All;
        ok &= Check("ui-smoke: ViewModel 改动回写下拉框", Equals(combo.SelectedValue, DurationFilter.All));

        vm.CategorySite = SiteType.XVideos;
        ok &= Check("ui-smoke: XVideos 分类页不显示时长筛选", !vm.ShowCategoryDurationFilter);
        vm.CategorySite = SiteType.PinSe;
        ok &= Check("ui-smoke: 91pinse 分类页显示时长筛选", vm.ShowCategoryDurationFilter);

        return ok;
    }

    /// <summary>
    /// 结果列表的右键菜单「打开源网址」。
    ///
    /// 菜单挂在行上而不是整个列表上，命令要从行拿到 ViewModel：绑定链是
    /// 行.Tag = ListView.DataContext(=VM)，菜单项再通过 PlacementTarget 取。
    /// 这条链断了菜单就会是灰的，所以这里断言 Tag 确实指向 VM。
    /// </summary>
    private static bool CheckResultContextMenus(MainViewModel vm)
    {
        for (var i = 1; i <= 3; i++)
            vm.SearchResults.Add(new SearchResult
            {
                Title = $"搜索结果 {i}",
                Url = $"https://www.xvideos.com/video.test{i}/x",
                Site = SiteType.XVideos
            });

        var searchTab = new SearchTab { DataContext = vm };
        searchTab.Measure(new Size(TabWidth, TabHeight));
        searchTab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
        searchTab.UpdateLayout();

        var categoryTab = new CategoryTab { DataContext = vm };
        categoryTab.Measure(new Size(TabWidth, TabHeight));
        categoryTab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
        categoryTab.UpdateLayout();

        var ok = CheckResultMenu("搜索", searchTab, vm.SearchResults, vm);
        ok &= CheckResultMenu("分类", categoryTab, vm.CategoryResults, vm);
        return ok;
    }

    private static bool CheckResultMenu(string label, DependencyObject root,
        System.Collections.IEnumerable items, MainViewModel vm)
    {
        var list = FindDescendant<ListView>(root, lv => ReferenceEquals(lv.ItemsSource, items));
        if (!Check($"ui-smoke: {label}结果列表已绑定", list != null)) return false;

        Check($"ui-smoke: {label}结果有内容", list!.Items.Count > 0);
        var row = list.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem;
        if (!Check($"ui-smoke: {label}结果行已生成", row != null)) return false;

        var menu = row!.ContextMenu;
        if (!Check($"ui-smoke: {label}结果行有右键菜单", menu != null)) return false;

        Check($"ui-smoke: {label}右键菜单只有一个「打开源网址」", menu!.Items.Count == 1);
        var item = menu.Items[0] as MenuItem;
        Console.WriteLine($"  info  {label}菜单项: \"{item?.Header}\"");
        Check($"ui-smoke: {label}菜单文案已本地化",
            (item?.Header as string) == (Application.Current.TryFindResource("Menu_OpenSourceUrl") as string));
        Check($"ui-smoke: {label}菜单能拿到 ViewModel（命令绑定链）", ReferenceEquals(row.Tag, vm));
        return true;
    }

    /// <summary>
    /// 渲染下载页并检查行内操作按钮：
    ///   下载中 → 取消
    ///   失败   → 重试 + 删除任务（线上 bug：这里只有“取消”，而取消对失败任务
    ///            是空操作，也没有删除入口）
    ///   已完成 → 删除任务 + 删除文件
    /// 随后走一遍真实的命令链路（删除 → 回收站 → 恢复），确认视图分类跟着变。
    /// </summary>
    private static bool CheckQueueTab(MainViewModel vm)
    {
        // 队列会从真实 %AppData% 数据库加载历史记录。历史一多，测试插入的行
        // 就会被挤出可视区域，容器生成不出来 → 断言假失败。先清空，保证
        // 这个测试只依赖自己造的数据。
        vm.DownloadItems.Clear();

        var running = new DownloadItem
        {
            Title = "下载中的任务", Url = "https://example.test/v/active",
            Status = "Downloading", IsActive = true, IsDownloading = true, Progress = 42, Speed = "1.2 MB/s"
        };
        var failed = new DownloadItem
        {
            Title = "hot stepdaughter took dick in her a...",
            Url = "https://example.test/v/failed",
            Status = "Failed: Response status code does not indicate success: 410 (Gone).",
            HasFailed = true, Progress = 8
        };
        var cancelled = new DownloadItem
        {
            Title = "被取消的任务", Url = "https://example.test/v/cancelled",
            Status = "Cancelled", IsCancelled = true
        };
        var completed = new DownloadItem
        {
            Title = "已完成的任务", Url = "https://example.test/v/done",
            Status = "Completed", IsCompleted = true, Progress = 100,
            OutputPath = Path.Combine(Path.GetTempPath(), "vf-ui-smoke-done.mp4")
        };

        foreach (var i in new[] { running, failed, cancelled, completed }) vm.DownloadItems.Add(i);

        var tab = new QueueTab { DataContext = vm };
        tab.Measure(new Size(TabWidth, TabHeight));
        tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
        tab.UpdateLayout();

        var ok = true;
        var list = FindDescendant<ListView>(tab, lv => ReferenceEquals(lv.ItemsSource, vm.QueueItemsView));
        Check("ui-smoke: 队列列表已绑定", list != null);
        if (list == null) return false;

        string Btn(string key) => Application.Current.TryFindResource(key) as string ?? $"[{key}]";

        var runningButtons = VisibleButtons(list, running);
        var failedButtons = VisibleButtons(list, failed);
        var completedButtons = VisibleButtons(list, completed);
        Console.WriteLine($"  info  失败行按钮: {string.Join(" / ", failedButtons)}");

        ok &= Check("ui-smoke: 下载中行显示取消",
            runningButtons.Count == 1 && runningButtons[0] == Btn("Btn_CancelDownload"));
        ok &= Check("ui-smoke: 失败行显示重试",
            failedButtons.Contains(Btn("Btn_RetryTask")));
        ok &= Check("ui-smoke: 失败行显示删除任务",
            failedButtons.Contains(Btn("Btn_DeleteTask")));
        ok &= Check("ui-smoke: 失败行不再显示取消（点了没反应的那个）",
            !failedButtons.Contains(Btn("Btn_CancelDownload")));
        ok &= Check("ui-smoke: 失败行不显示删除文件",
            !failedButtons.Contains(Btn("Btn_DeleteLocalFile")));
        ok &= Check("ui-smoke: 已完成行显示删除任务 + 删除文件",
            completedButtons.Contains(Btn("Btn_DeleteTask")) &&
            completedButtons.Contains(Btn("Btn_DeleteLocalFile")));

        // 截图（修复后应该看到 重试 / 删除任务）
        var bmp = new RenderTargetBitmap((int)TabWidth, (int)TabHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(tab);
        var path = Path.Combine(AppContext.BaseDirectory, "ui-smoke-queuetab.png");
        using (var fs = File.Create(path))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(fs);
        }
        ok &= Check("ui-smoke: 下载页截图非空白", !IsBlank(bmp));
        Console.WriteLine($"  info  screenshot: {path}");

        // 走真实命令链路：失败任务 → 删除任务 → 回收站 → 恢复 → 回到失败视图
        ok &= Check("ui-smoke: 失败任务出现在「失败」视图",
            InView(vm, QueueFilter.Failed, failed) && !InView(vm, QueueFilter.Failed, completed));
        ok &= Check("ui-smoke: 已取消任务也出现在「失败」视图",
            InView(vm, QueueFilter.Failed, cancelled));

        vm.DeleteTaskCommand.Execute(failed);
        ok &= Check("ui-smoke: 删除后进入「已删除」视图", InView(vm, QueueFilter.Deleted, failed));
        ok &= Check("ui-smoke: 删除后从「全部」消失", !InView(vm, QueueFilter.All, failed));

        vm.RestoreTaskCommand.Execute(failed);
        ok &= Check("ui-smoke: 恢复后回到「失败」视图（不再死循环在回收站）",
            InView(vm, QueueFilter.Failed, failed) && !InView(vm, QueueFilter.Deleted, failed));

        vm.QueueFilter = QueueFilter.All;
        return ok;
    }

    /// <summary>某一行当前实际可见的按钮文字（按 Visibility 判定，与绑定一致）。</summary>
    private static List<string> VisibleButtons(ListView list, object item)
    {
        var result = new List<string>();
        var index = list.Items.IndexOf(item);
        if (index < 0) return result;
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is not ListViewItem row) return result;
        CollectVisibleButtons(row, result);
        return result;
    }

    private static void CollectVisibleButtons(DependencyObject root, List<string> acc)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button b && b.Content is string text && IsEffectivelyVisible(b))
                acc.Add(text);
            CollectVisibleButtons(child, acc);
        }
    }

    /// <summary>
    /// 祖先折叠时，子元素自身的 Visibility 仍然是 Visible —— 只看自己会
    /// 把整组被折叠的按钮误判成“可见”，所以必须一路检查到行的根部。
    /// </summary>
    private static bool IsEffectivelyVisible(DependencyObject node)
    {
        for (var current = node; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement element && element.Visibility != Visibility.Visible)
                return false;
            if (current is ListViewItem) break;   // 行边界之外的元素不算
        }
        return true;
    }

    private static bool InView(MainViewModel vm, QueueFilter filter, DownloadItem item)
    {
        vm.QueueFilter = filter;
        return vm.QueueItemsView.Cast<DownloadItem>().Contains(item);
    }

    private static bool IsBlank(BitmapSource bmp)
    {
        var stride = bmp.PixelWidth * 4;
        var pixels = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(pixels, stride, 0);
        // Sample every 97th pixel: if any channel differs from the first pixel,
        // we drew content.
        for (var i = 0; i < pixels.Length - 4; i += 4 * 97)
        {
            if (pixels[i] != pixels[0] || pixels[i + 1] != pixels[1] || pixels[i + 2] != pixels[2])
                return false;
        }
        return true;
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && predicate(typed)) return typed;
            var found = FindDescendant(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

    private static bool Check(string name, bool condition)
    {
        // Reuse CategoryTests' counter style: pass/fail printed individually.
        Console.WriteLine(condition ? $"  PASS  {name}" : $"  FAIL  {name}");
        if (!condition) _failed++;
        return condition;
    }

    private static int _failed;
    public static int Failures => _failed;
}

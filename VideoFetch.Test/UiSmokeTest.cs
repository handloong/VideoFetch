using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoFetch.Models;
using VideoFetch.ViewModels;
using VideoFetch.Services;
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

        // 队列会把未完成任务写进 queue.json。测试里加的都是假任务，
        // 一旦写进真实目录，下次启动就会去下载 example.test —— 全程指向临时目录。
        var pendingDir = Path.Combine(Path.GetTempPath(), "vf-ui-pending-" + Guid.NewGuid().ToString("N")[..8]);
        var originalPendingDir = PendingQueueStore.Directory;
        PendingQueueStore.Directory = pendingDir;

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
        var bmp = RenderOnThemeBackground(tab, (int)TabWidth, (int)TabHeight);
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

        // 4b) 队列排序 + 多选删除文件
        ok &= CheckQueueSortAndDelete(vm);

        // 4c) 内置播放器：音量记忆 + 播放时机
        ok &= CheckPlayer();

        // 6) 搜索结果 / 分类结果的行右键菜单
        ok &= CheckResultContextMenus(vm);

        // 7) 时长筛选下拉框（仅 91pinse）
        ok &= CheckDurationFilter(vm);

        // 8) 结果列表排序（时长 长→短）
        ok &= CheckResultSort(vm);

        // 8b) 结果列表：Ctrl 点选 / Shift 连选 等同勾选
        ok &= CheckResultRowSelection(vm);

        // 9) 批量下载的进度与取消（批量期间界面上必须有反馈）
        ok &= CheckBatchUi(vm);

        // 9) 弹层主题（右键菜单 / ToolTip 都是独立视觉树，不继承主题）
        ok &= CheckPopupThemes();

        // 10) 设置页的 Cookie 导入
        ok &= CheckCookieSettings(vm);

        // 11) UI 巡检：多尺寸下找裁切/零尺寸/越界/重叠/对比度问题
        // 有发现即算失败 —— 逼着每次新增界面元素都把布局/对比度当回事
        ok &= Check("ui-smoke: UI 巡检（多尺寸布局与对比度）无问题", UiAudit.Run(vm) == 0);

        // 收尾：队列的待续清单目录还原，临时目录删掉
        PendingQueueStore.Directory = originalPendingDir;
        try { if (Directory.Exists(pendingDir)) Directory.Delete(pendingDir, true); } catch { }

        return ok;
    }

    /// <summary>
    /// 结果列表排序：搜索页/分类页都要有下拉框，切换后列表真的重排，
    /// 而且序号跟着新的可视顺序重编（序号是装饰，不能还挂在旧位置）。
    ///
    /// 注意：这里会触发一次设置保存（排序是会被记住的偏好），所以必须
    /// 在 Cookie 测试之前跑，并把它复位成默认值。
    /// </summary>
    private static bool CheckResultSort(MainViewModel vm)
    {
        var ok = true;

        // 排序偏好是会被记住的：上次跑测试（或用户用过）留下的值会带进 ViewModel。
        // 先复位，别让这个用例依赖机器上的 settings.json。
        vm.ResultSort = ResultSort.Default;

        ok &= Check("ui-smoke: 排序选项 3 项", vm.SortOptions.Count == 3, vm.SortOptions.Count.ToString());
        ok &= Check("ui-smoke: 排序文案来自语言资源",
            vm.SortOptions[0].Label == (Application.Current.TryFindResource("Sort_Default") as string),
            vm.SortOptions[0].Label);
        ok &= Check("ui-smoke: 排序选项的 ToString 是文案（否则下拉框会显示整条记录）",
            !vm.SortOptions[1].ToString().Contains("SortOption"), vm.SortOptions[1].ToString());

        ok &= Check("ui-smoke: 默认排序是站点原序", vm.ResultSort == ResultSort.Default);

        // 故意打乱：300s / 未知 / 5400s / 2700s
        vm.SearchResults.Clear();
        foreach (var (title, duration) in new[]
                 {
                     ("短", "5 min"), ("未知", ""), ("最长", "1:30:00"), ("中等", "45 min")
                 })
        {
            vm.SearchResults.Add(new SearchResult
            {
                Title = title, Url = "https://site.test/v/" + title,
                Duration = duration, Site = SiteType.XVideos
            });
        }

        var searchTab = new SearchTab { DataContext = vm };
        searchTab.Measure(new Size(TabWidth, TabHeight));
        searchTab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
        searchTab.UpdateLayout();

        var combo = FindDescendant<ComboBox>(searchTab,
            c => ReferenceEquals(c.ItemsSource, vm.SortOptions));
        ok &= Check("ui-smoke: 搜索页排序下拉框已绑定", combo != null);
        ok &= Check("ui-smoke: 排序下拉框可见", combo != null && IsEffectivelyVisible(combo));
        ok &= Check("ui-smoke: 下拉框显示当前排序",
            combo == null || combo.SelectedItem?.ToString() == vm.SortOptions[0].Label,
            combo?.SelectedItem?.ToString());

        // 通过下拉框改（走真实绑定链：SelectedValue -> ViewModel -> 重排）
        if (combo != null)
        {
            combo.SelectedValue = ResultSort.DurationDesc;
            combo.UpdateLayout();
        }

        ok &= Check("ui-smoke: 下拉框选择同步到 ViewModel", vm.ResultSort == ResultSort.DurationDesc);

        var order = vm.SearchResults.Select(r => r.Title).ToArray();
        ok &= Check("ui-smoke: 列表按时长从长到短重排",
            string.Join(",", order) == "最长,中等,短,未知", string.Join(",", order));
        ok &= Check("ui-smoke: 重排后序号跟着可视顺序",
            vm.SearchResults.Select(r => r.Index).SequenceEqual(new[] { 1, 2, 3, 4 }),
            string.Join(",", vm.SearchResults.Select(r => r.Index)));

        // 分类页也要有同一个控件
        var categoryTab = new CategoryTab { DataContext = vm };
        categoryTab.Measure(new Size(TabWidth, TabHeight));
        categoryTab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
        categoryTab.UpdateLayout();
        ok &= Check("ui-smoke: 分类页排序下拉框已绑定",
            FindDescendant<ComboBox>(categoryTab, c => ReferenceEquals(c.ItemsSource, vm.SortOptions)) != null);

        // 截图留档（排序控件在列表上方右侧）
        var bmp = RenderOnThemeBackground(searchTab, (int)TabWidth, (int)TabHeight);
        var shot = Path.Combine(AppContext.BaseDirectory, "ui-smoke-sort.png");
        using (var fs = File.Create(shot))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            encoder.Save(fs);
        }
        Console.WriteLine($"  info  screenshot: {shot}");

        // 复位，避免把测试数据写进配置
        vm.ResultSort = ResultSort.Default;
        vm.SearchResults.Clear();

        return ok;
    }

    /// <summary>
    /// 结果列表的行选中要等于"勾选下载"：Ctrl 点选 / Shift 连选之后，
    /// 用户不用再逐行去点复选框。
    /// </summary>
    private static bool CheckResultRowSelection(MainViewModel vm)
    {
        var ok = true;

        var items = Enumerable.Range(1, 3).Select(i => new SearchResult
        {
            Title = $"选中项 {i}",
            Url = $"https://example.test/sel{i}",
            Site = SiteType.XVideos
        }).ToList();

        foreach (var item in items) vm.SearchResults.Add(item);

        var tab = new SearchTab { DataContext = vm };
        tab.Measure(new Size(TabWidth, TabHeight));
        tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
        tab.UpdateLayout();

        var list = FindDescendant<ListView>(tab, l => ReferenceEquals(l.ItemsSource, vm.SearchResults));
        if (list == null) return false;

        ok &= Check("ui-smoke: 搜索结果支持多选", list.SelectionMode == SelectionMode.Extended,
            list.SelectionMode.ToString());

        // 模拟 Ctrl 点选两条（SelectedItems 变化会触发 SelectionChanged）
        list.SelectedItems.Add(items[0]);
        list.SelectedItems.Add(items[2]);

        ok &= Check("ui-smoke: 选中行即勾选",
            items[0].IsSelected && items[2].IsSelected && !items[1].IsSelected,
            string.Join(",", items.Select(i => i.IsSelected)));
        ok &= Check("ui-smoke: 批量下载数量跟着变", vm.SelectedResultCount == 2,
            vm.SelectedResultCount.ToString());

        list.SelectedItems.Remove(items[2]);
        ok &= Check("ui-smoke: 取消选中即取消勾选", !items[2].IsSelected,
            items[2].IsSelected.ToString());

        // 逐行勾选也应该照常工作（反向不受行选中影响）
        items[1].IsSelected = true;
        ok &= Check("ui-smoke: 复选框仍可单独勾选", vm.SelectedResultCount == 2 && items[1].IsSelected,
            vm.SelectedResultCount.ToString());

        items.ForEach(i => i.IsSelected = false);
        vm.SearchResults.Clear();
        return ok;
    }

    /// <summary>
    /// 队列排序（点表头）与多选删除本地文件。
    ///
    /// 排序键、自然方向、再次点击反向，这三件事都得对；删除只能作用于
    /// 真正有文件的已完成任务，失败/下载中的不能被误删。
    /// </summary>
    private static bool CheckQueueSortAndDelete(MainViewModel vm)
    {
        var ok = true;
        var root = Path.Combine(Path.GetTempPath(), "vf-queue-del-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        DownloadItem Make(string title, string duration, long size, DateTime added)
        {
            var path = Path.Combine(root, title + ".mp4");
            File.WriteAllText(path, "x");
            return new DownloadItem
            {
                Title = title,
                Url = "https://example.test/v/" + title,
                OutputPath = path,
                State = DownloadState.Completed,
                IsCompleted = true,
                Duration = duration,
                FileSize = size,
                AddedAt = added
            };
        }

        // 三条：大小/时长/时间 三者顺序故意各不相同
        var a = Make("A", "1 min", 300, new DateTime(2026, 10, 20, 10, 0, 0));
        var b = Make("B", "3 min", 100, new DateTime(2026, 10, 22, 10, 0, 0));
        var c = Make("C", "2 min", 200, new DateTime(2026, 10, 21, 10, 0, 0));
        var failed = new DownloadItem
        {
            Title = "F", Url = "https://example.test/v/F", State = DownloadState.Failed, HasFailed = true
        };

        foreach (var i in new[] { a, b, c, failed }) vm.DownloadItems.Add(i);

        var tab = new QueueTab { DataContext = vm };
        tab.Measure(new Size(TabWidth, TabHeight));
        tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
        tab.UpdateLayout();

        var list = FindDescendant<ListView>(tab, l => ReferenceEquals(l.ItemsSource, vm.QueueItemsView));
        if (list == null) return false;

        string Order() => string.Join(",", list.Items.Cast<DownloadItem>()
            .Where(i => i.Title is "A" or "B" or "C")
            .Select(i => i.Title));

        ok &= Check("queue: 支持多选", list.SelectionMode == SelectionMode.Extended,
            list.SelectionMode.ToString());

        ok &= Check("queue: 默认按下载时间倒序", Order() == "B,C,A", Order());
        ok &= Check("queue: 排序提示文字",
            vm.QueueSortLabel.Contains(Res("Queue_Header_DownloadTime")) && vm.QueueSortLabel.Contains("↓"),
            vm.QueueSortLabel);

        // 点「大小」：数值列自然方向是降序
        ClickHeader(list, Res("Queue_Header_Size"));
        ok &= Check("queue: 点大小表头切到该列",
            vm.QueueSortKey == QueueSortKey.Size && vm.QueueSortDescending,
            $"{vm.QueueSortKey}/{vm.QueueSortDescending}");
        ok &= Check("queue: 大小降序", Order() == "A,C,B", Order());

        // 同一列再点一次：反向
        ClickHeader(list, Res("Queue_Header_Size"));
        ok &= Check("queue: 再点一次变升序", !vm.QueueSortDescending && Order() == "B,C,A", Order());

        // 文字列自然方向是升序
        ClickHeader(list, Res("Queue_Header_Title"));
        ok &= Check("queue: 标题列默认升序",
            vm.QueueSortKey == QueueSortKey.Title && !vm.QueueSortDescending && Order() == "A,B,C",
            $"{vm.QueueSortKey}/{vm.QueueSortDescending}/{Order()}");

        // 点「操作」列（没有排序键）不能把表头点坏
        ClickHeader(list, Res("Queue_Header_Actions"));
        ok &= Check("queue: 非排序列点了不变", vm.QueueSortKey == QueueSortKey.Title);

        // ── 多选删除文件 ──
        vm.UpdateQueueSelection(new[] { a, b, c });
        ok &= Check("queue: 选中 3 个已完成任务", vm.DeletableSelectionCount == 3,
            vm.DeletableSelectionCount.ToString());

        vm.UpdateQueueSelection(new[] { a, b, failed });
        ok &= Check("queue: 失败任务不计入可删", vm.DeletableSelectionCount == 2,
            vm.DeletableSelectionCount.ToString());

        tab.UpdateLayout();
        var deleteBtn = FindDescendant<Button>(tab, b2 => (b2.Content as string) == vm.DeleteFilesText);
        ok &= Check("queue: 多选后出现批量删除按钮",
            deleteBtn != null && IsEffectivelyVisible(deleteBtn), vm.DeleteFilesText);

        vm.DeleteSelectedFilesCommand.Execute(null);

        ok &= Check("queue: 选中的文件被删掉", !File.Exists(a.OutputPath) && !File.Exists(b.OutputPath));
        ok &= Check("queue: 没选中的文件还在", File.Exists(c.OutputPath));
        ok &= Check("queue: 删除后标记为文件丢失", a.IsFileMissing && b.IsFileMissing);
        ok &= Check("queue: 删完按钮消失", vm.DeletableSelectionCount == 0,
            vm.DeletableSelectionCount.ToString());

        // 复位：排序回默认，测试数据清掉
        vm.SortQueueBy(QueueSortKey.DownloadTime);
        foreach (var i in new[] { a, b, c, failed }) vm.DownloadItems.Remove(i);
        try { Directory.Delete(root, true); } catch { }

        return ok;
    }

    /// <summary>从语言资源取文案（新方法里不能用别处的局部函数）。</summary>
    private static string Res(string key)
        => Application.Current.TryFindResource(key) as string ?? key;

    /// <summary>模拟点击某个表头（按表头文字找，找不到就跳过）。</summary>
    private static void ClickHeader(ListView list, string headerText)
    {
        var header = FindDescendant<GridViewColumnHeader>(list, h => (h.Content as string) == headerText);
        header?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, header));
    }

    /// <summary>
    /// 内置播放器的两件事：
    /// ① 音量要记住（恢复上次的值，改完关窗写回配置）；
    /// ② 播放请求要等到窗口加载+媒体就绪才执行 —— 之前 Play() 在 Show() 之前调用，
    ///    MediaElement 不在视觉树里，Play 命令被丢弃，表现是"停在第一帧，拖一下才播"。
    ///
    /// 真实媒体文件无法在测试里播放（没有解码器），所以这里验证的是状态机：
    /// 请求播放后、媒体就绪前，不能谎报"正在播放"。
    /// </summary>
    private static bool CheckPlayer()
    {
        var ok = true;

        var original = SettingsService.Directory;
        var dir = Path.Combine(Path.GetTempPath(), "vf-player-" + Guid.NewGuid().ToString("N")[..8]);
        SettingsService.Directory = dir;

        try
        {
            var settings = new AppSettings { PlayerVolume = 0.35 };
            var player = new VideoPlayerWindow(settings);
            var slider = player.FindName("VolumeSlider") as Slider;
            var media = player.FindName("Media") as MediaElement;

            ok &= Check("player: 音量控件已就绪", slider != null && media != null);
            if (slider == null || media == null) return false;

            ok &= Check("player: 恢复上次音量",
                Math.Abs(slider.Value - 35) < 0.01 && Math.Abs(media.Volume - 0.35) < 0.01,
                $"slider={slider.Value} volume={media.Volume}");

            // 拖音量条 → 立刻写回设置对象（落盘由防抖定时器/关窗负责）
            slider.Value = 60;
            ok &= Check("player: 改音量同步到设置",
                Math.Abs(settings.PlayerVolume - 0.60) < 0.01, settings.PlayerVolume.ToString("0.00"));
            ok &= Check("player: 改音量同步到播放器",
                Math.Abs(media.Volume - 0.60) < 0.01, media.Volume.ToString("0.00"));

            // 播放时机：窗口还没显示，不能谎报在播放（这正是"双击不播"的根因）
            player.Play(Path.Combine(Path.GetTempPath(), "vf-player-not-real.mp4"));
            ok &= Check("player: 未加载时不谎报正在播放", !player.IsPlaying);
            ok &= Check("player: 已绑定媒体源", media.Source != null, media.Source?.ToString());

            // 关窗 → 音量落盘
            player.Close();
            var saved = new SettingsService().Load();
            ok &= Check("player: 关闭时把音量写进配置",
                Math.Abs(saved.PlayerVolume - 0.60) < 0.01, saved.PlayerVolume.ToString("0.00"));
        }
        finally
        {
            SettingsService.Directory = original;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        return ok;
    }

    /// <summary>
    /// 设置页的 Cookie 导入：切站点要回填该站点已保存的 Cookie，输入框双向绑定。
    /// </summary>
    private static bool CheckCookieSettings(MainViewModel vm)
    {
        var ok = true;

        vm.Settings.SiteCookies["PinSe"] = "session=abc; uid=7";
        vm.Settings.SiteCookies["XVideos"] = string.Empty;

        vm.CookieSite = SiteType.XVideos;
        ok &= Check("ui-smoke: 切到未配置的站点时输入框为空", vm.CookieText.Length == 0, vm.CookieText);

        vm.CookieSite = SiteType.PinSe;
        ok &= Check("ui-smoke: 切站点回填已保存的 Cookie",
            vm.CookieText == "session=abc; uid=7", vm.CookieText);

        var tab = new SettingsTab { DataContext = vm };
        tab.Measure(new Size(TabWidth, TabHeight));
        tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
        tab.UpdateLayout();

        var combo = FindDescendant<ComboBox>(tab, c => ReferenceEquals(c.ItemsSource, vm.CookieSites));
        ok &= Check("ui-smoke: Cookie 站点下拉框已绑定", combo != null);
        ok &= Check("ui-smoke: Cookie 站点下拉框有 4 项", combo?.Items.Count == 4);

        var box = FindDescendant<TextBox>(tab,
            t => ReferenceEquals(t.DataContext, vm)
                 && t.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path
                    == nameof(MainViewModel.CookieText));
        ok &= Check("ui-smoke: Cookie 输入框已绑定", box != null);
        ok &= Check("ui-smoke: 输入框显示当前 Cookie", box?.Text == vm.CookieText, box?.Text);

        // 清理，避免影响别的检查
        vm.Settings.SiteCookies.Clear();
        vm.CookieSite = SiteType.PornHub;
        return ok;
    }

    /// <summary>
    /// 批量下载逐条抓页面要几十秒，界面必须给出进度、也能中途取消。
    /// 这里验证进度文字和取消按钮确实随 IsBatchDownloading 显示/隐藏。
    /// </summary>
    private static bool CheckBatchUi(MainViewModel vm)
    {
        // 批量栏整条挂在 HasSearchResults 上；前面的排序测试会清空列表，
        // 这里自己补两行，别让测试之间互相依赖顺序。
        if (vm.SearchResults.Count == 0)
        {
            for (var i = 1; i <= 2; i++)
                vm.SearchResults.Add(new SearchResult
                {
                    Title = $"批量用例 {i}",
                    Url = $"https://site.test/v/batch{i}",
                    Duration = "10 min",
                    Site = SiteType.XVideos
                });
        }

        var tab = new SearchTab { DataContext = vm };
        tab.Measure(new Size(TabWidth, TabHeight));
        tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
        tab.UpdateLayout();

        string Str(string key) => Application.Current.TryFindResource(key) as string ?? $"[{key}]";

        var cancelBtn = FindDescendant<Button>(tab, b => (b.Content as string) == Str("Btn_CancelBatch"));
        var progress = FindDescendant<TextBlock>(tab, t => ReferenceEquals(t.DataContext, vm)
                                                        && t.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path.Path
                                                           == nameof(MainViewModel.BatchProgressText));

        var ok = Check("ui-smoke: 批量取消按钮存在", cancelBtn != null);
        ok &= Check("ui-smoke: 批量进度文字存在", progress != null);
        if (cancelBtn == null || progress == null) return false;

        ok &= Check("ui-smoke: 空闲时不显示取消按钮", !IsEffectivelyVisible(cancelBtn));
        ok &= Check("ui-smoke: 空闲时不显示进度文字", !IsEffectivelyVisible(progress));

        vm.IsBatchDownloading = true;
        vm.BatchProgressText = "正在准备 3/20：某个视频";
        tab.UpdateLayout();
        ok &= Check("ui-smoke: 批量中显示取消按钮", IsEffectivelyVisible(cancelBtn));
        ok &= Check("ui-smoke: 批量中显示进度文字", IsEffectivelyVisible(progress));

        vm.IsBatchDownloading = false;
        vm.BatchProgressText = string.Empty;
        tab.UpdateLayout();
        ok &= Check("ui-smoke: 结束后恢复隐藏",
            !IsEffectivelyVisible(cancelBtn) && !IsEffectivelyVisible(progress));

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
        var bmp = RenderOnThemeBackground(tab, (int)TabWidth, (int)TabHeight);
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
            State = DownloadState.Downloading, IsActive = true, IsDownloading = true, Progress = 42, Speed = "1.2 MB/s"
        };
        var failed = new DownloadItem
        {
            Title = "hot stepdaughter took dick in her a...",
            Url = "https://example.test/v/failed",
            State = DownloadState.Failed,
            LastError = "Response status code does not indicate success: 410 (Gone).",
            HasFailed = true, Progress = 8
        };
        var cancelled = new DownloadItem
        {
            Title = "被取消的任务", Url = "https://example.test/v/cancelled",
            State = DownloadState.Cancelled, IsCancelled = true
        };
        var completed = new DownloadItem
        {
            Title = "已完成的任务", Url = "https://example.test/v/done",
            State = DownloadState.Completed, IsCompleted = true, Progress = 100,
            Duration = "0:02:14",
            FileSize = 12897485,          // 12.3M
            AddedAt = new DateTime(2026, 10, 23, 9, 5, 0),
            CompletedAt = new DateTime(2026, 10, 23, 9, 7, 0),
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

        // 状态文案必须本地化（以前中文界面里直接显示 Downloading / Failed: ...）
        Console.WriteLine($"  info  失败行状态: \"{failed.Status}\"");
        ok &= Check("ui-smoke: 状态文案已本地化（下载中）",
            running.Status == Btn("State_Downloading"), running.Status);
        ok &= Check("ui-smoke: 失败状态带本地化前缀和原因",
            failed.Status == $"{Btn("State_Failed")}: {failed.LastError}", failed.Status);
        ok &= Check("ui-smoke: 已完成文案已本地化",
            completed.Status == Btn("State_Completed"), completed.Status);

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
        var bmp = RenderOnThemeBackground(tab, (int)TabWidth, (int)TabHeight);
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

        // 「全部取消」：以前 CancelAll 是死代码，界面上根本没有入口
        running.IsDownloading = false;
        running.IsActive = false;
        var queued = new DownloadItem
        {
            Title = "排队中的任务", Url = "https://example.test/v/queued",
            State = DownloadState.Queued, IsActive = true
        };
        vm.DownloadItems.Add(queued);

        ok &= Check("ui-smoke: 有活跃任务时显示全部取消", vm.ActiveCount > 0, vm.ActiveCount.ToString());
        var cancelAll = FindDescendant<Button>(tab, b => (b.Content as string) == Btn("Btn_CancelAll"));
        ok &= Check("ui-smoke: 全部取消按钮存在",
            cancelAll != null && IsEffectivelyVisible(cancelAll));

        vm.CancelAllDownloadsCommand.Execute(null);
        ok &= Check("ui-smoke: 全部取消后没有活跃任务", vm.ActiveCount == 0, vm.ActiveCount.ToString());
        ok &= Check("ui-smoke: 排队任务被标记为已取消",
            queued.State == DownloadState.Cancelled, queued.State.ToString());

        // 表格新增两列：时长 + 下载时间。
        // 前面几步把视图停在「已删除」，列表是空的 → 先切回「全部」再断言单元格。
        vm.QueueFilter = QueueFilter.All;
        tab.UpdateLayout();

        var queueList = FindDescendant<ListView>(tab, l => ReferenceEquals(l.ItemsSource, vm.QueueItemsView));
        var columns = (queueList?.View as GridView)?.Columns
            .Select(c => c.Header as string ?? string.Empty).ToList() ?? new List<string>();
        ok &= Check("ui-smoke: 队列表格有「时长」列", columns.Contains(Btn("Queue_Header_Duration")),
            string.Join(" | ", columns));
        ok &= Check("ui-smoke: 队列表格有「下载时间」列",
            columns.Contains(Btn("Queue_Header_DownloadTime")));
        ok &= Check("ui-smoke: 队列表格有「大小」列",
            columns.Contains(Btn("Queue_Header_Size")), string.Join(" | ", columns));

        // 标题一长就被截断，所以列宽必须能拖、横向必须能滚
        // 注意：表头行末尾还有一个"填充表头"（Column 为 null），别选中它
        var grip = FindDescendant<GridViewColumnHeader>(tab, h => h.Column != null);
        ok &= Check("ui-smoke: 表头有拖拽把手（列宽可调）",
            grip?.Template?.FindName("PART_HeaderGrip", grip) != null);
        // 真的模拟一次拖拽：旧模板只有光标变化、列宽不动，这条断言能抓住
        if (grip != null && queueList?.View is GridView gv && gv.Columns.Count > 0)
        {
            var thumb = (Thumb?)grip.Template.FindName("PART_HeaderGrip", grip);
            var column = grip.Column!;
            var before = column.Width;
            thumb?.RaiseEvent(new DragDeltaEventArgs(40, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            ok &= Check("ui-smoke: 拖拽把手真的改变列宽",
                Math.Abs(column.Width - (before + 40)) < 0.5,
                $"{before} -> {column.Width}");
            column.Width = before;
        }
        ok &= Check("ui-smoke: 队列列表允许横向滚动",
            ScrollViewer.GetHorizontalScrollBarVisibility(list) == ScrollBarVisibility.Auto,
            ScrollViewer.GetHorizontalScrollBarVisibility(list).ToString());
        ok &= Check("ui-smoke: 队列列表允许纵向滚动",
            ScrollViewer.GetVerticalScrollBarVisibility(list) == ScrollBarVisibility.Auto);

        // 窗口变窄时横向滚动条要真的铺开。主题里曾把 ScrollBar 的 Width 锁成 6，
        // 横向的那条就被压成一个小白块，既看不见也拖不动。
        var narrow = new QueueTab { DataContext = vm };
        narrow.Measure(new Size(640, 420));
        narrow.Arrange(new Rect(0, 0, 640, 420));
        narrow.UpdateLayout();
        var hbar = FindDescendant<ScrollBar>(narrow,
            sb => sb.Orientation == Orientation.Horizontal && sb.Visibility == Visibility.Visible);
        ok &= Check("ui-smoke: 横向滚动条铺开而不是挤成方块",
            hbar != null && hbar.ActualWidth > 200,
            hbar == null ? "没找到横向滚动条" : $"{hbar.ActualWidth:0}x{hbar.ActualHeight:0}");

        var narrowBmp = RenderOnThemeBackground(narrow, 640, 420);
        var narrowPath = Path.Combine(AppContext.BaseDirectory, "ui-smoke-narrow.png");
        using (var fs = File.Create(narrowPath))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(narrowBmp));
            encoder.Save(fs);
        }

        // 单元格真的渲染出了内容（只断言列头，绑定断了也发现不了）
        var completedRow = vm.DownloadItems.FirstOrDefault(i => i.IsCompleted);
        if (completedRow != null)
        {
            ok &= Check("ui-smoke: 已完成行时长文案", completedRow.DurationText == "0:02:14",
                completedRow.DurationText);
            ok &= Check("ui-smoke: 下载时间用完成时刻",
                completedRow.DownloadTimeText == "2026-10-23 09:07", completedRow.DownloadTimeText);

            // 单元格真的渲染出来了（虚拟化下只查可见行）
            ok &= Check("ui-smoke: 时长列渲染出 0:02:14",
                FindDescendant<TextBlock>(tab, t => t.Text == "0:02:14") != null);
            ok &= Check("ui-smoke: 大小列用最大单位渲染（12.3M）",
                FindDescendant<TextBlock>(tab, t => t.Text == "12.3M") != null);
            ok &= Check("ui-smoke: 下载时间列渲染出 2026-10-23 09:07",
                FindDescendant<TextBlock>(tab, t => t.Text == "2026-10-23 09:07") != null);
        }

        // 队列按下载时间倒序（最新在最上面）
        var orderedTimes = list.Items.Cast<DownloadItem>().Select(i => i.DownloadTime).ToList();
        ok &= Check("ui-smoke: 队列按下载时间倒序",
            orderedTimes.SequenceEqual(orderedTimes.OrderByDescending(t => t)),
            string.Join(" | ", orderedTimes.Select(t => t.ToString("HH:mm:ss"))));

        // 「重试全部」：失败/已取消的一键重新排队。        // 这里只验证按钮存在、可见、绑到真命令 —— 真按下去会启动真实下载，
        // 队列行为本身由 DownloadQueueTests 的 retry-all 覆盖。
        ok &= Check("ui-smoke: 有失败任务时 FailedCount > 0", vm.FailedCount > 0, vm.FailedCount.ToString());
        var retryAll = FindDescendant<Button>(tab, b => (b.Content as string) == Btn("Btn_RetryAll"));
        ok &= Check("ui-smoke: 重试全部按钮存在", retryAll != null);
        ok &= Check("ui-smoke: 重试全部按钮可见", retryAll != null && IsEffectivelyVisible(retryAll));
        ok &= Check("ui-smoke: 重试全部按钮绑到对应命令",
            retryAll != null && ReferenceEquals(retryAll.Command, vm.RetryAllFailedCommand));

        // 没有失败任务时必须消失，不能留个点了没反应的按钮占位
        foreach (var i in vm.DownloadItems)
        {
            i.HasFailed = false;
            i.IsCancelled = false;
        }
        tab.UpdateLayout();
        ok &= Check("ui-smoke: 没有失败任务时隐藏重试全部",
            retryAll != null && !IsEffectivelyVisible(retryAll));

        vm.QueueFilter = QueueFilter.All;
        return ok;
    }



    /// <summary>
    /// 离屏渲染一个控件。
    ///
    /// 必须先铺一层主题底色：UserControl 本身没有 Background，直接渲染出来的
    /// 背景是透明（A=0），看图时会被合成成白色，深色主题的文字就糊成一片。
    /// </summary>
    private static RenderTargetBitmap RenderOnThemeBackground(FrameworkElement element, int width, int height)
    {
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        var background = Application.Current.TryFindResource("BgPrimaryBrush") as Brush
                         ?? new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x1E));
        var layer = new DrawingVisual();
        using (var dc = layer.RenderOpen())
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));

        bmp.Render(layer);
        bmp.Render(element);
        return bmp;
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

    private static bool Check(string name, bool condition, string? actual = null)
    {
        // Reuse CategoryTests' counter style: pass/fail printed individually.
        Console.WriteLine(condition
            ? $"  PASS  {name}"
            : $"  FAIL  {name}" + (actual is null ? "" : $"  (实际: {actual})"));
        if (!condition) _failed++;
        return condition;
    }

    private static int _failed;
    public static int Failures => _failed;
}

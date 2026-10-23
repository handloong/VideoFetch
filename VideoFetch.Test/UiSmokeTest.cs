using System;
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

        return ok;
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

    private static void Check(string name, bool condition)
    {
        // Reuse CategoryTests' counter style: pass/fail printed individually.
        Console.WriteLine(condition ? $"  PASS  {name}" : $"  FAIL  {name}");
        if (!condition) _failed++;
    }

    private static int _failed;
    public static int Failures => _failed;
}

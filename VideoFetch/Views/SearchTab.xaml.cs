using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using VideoFetch.Models;

namespace VideoFetch.Views;

public partial class SearchTab : UserControl
{
    public SearchTab() => InitializeComponent();

    /// <summary>
    /// 行选中即"勾选下载"：Ctrl 点选 / Shift 连选之后不用再去点每一行的复选框。
    ///
    /// 只做单向同步（选中 → IsSelected）：复选框自己切换时不该反过来改变行的选中状态，
    /// 否则点一个复选框会把整批选中状态搞乱。
    /// </summary>
    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.AddedItems.OfType<SearchResult>())
            item.IsSelected = true;

        foreach (var item in e.RemovedItems.OfType<SearchResult>())
            item.IsSelected = false;
    }

    private void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView lv || lv.SelectedItem is not SearchResult result) return;

        var mainWindow = System.Windows.Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;

        // 已下载 → 播放
        if (result.IsAlreadyDownloaded && !string.IsNullOrWhiteSpace(result.LocalFilePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = result.LocalFilePath,
                    UseShellExecute = true
                });
            }
            catch { }
            return;
        }

        // 未下载 → 缩略图预览
        if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            mainWindow.ShowImagePreview(result.ThumbnailUrl, result.Title);
    }
}

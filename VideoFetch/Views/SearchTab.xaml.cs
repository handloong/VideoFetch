using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using VideoFetch.Models;

namespace VideoFetch.Views;

public partial class SearchTab : UserControl
{
    public SearchTab() => InitializeComponent();

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

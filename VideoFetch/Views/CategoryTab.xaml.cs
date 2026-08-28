using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VideoFetch.Models;

namespace VideoFetch.Views;

public partial class CategoryTab : UserControl
{
    public CategoryTab() => InitializeComponent();

    private void CategoryResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView lv || lv.SelectedItem is not SearchResult result) return;

        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow == null) return;

        // 已下载 → 播放本地文件
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

        // 未下载 → 缩略图大图预览
        if (!string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            mainWindow.ShowImagePreview(result.ThumbnailUrl, result.Title);
    }
}

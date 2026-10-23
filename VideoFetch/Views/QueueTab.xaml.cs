using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VideoFetch.Models;
using VideoFetch.Services;
using VideoFetch.ViewModels;

namespace VideoFetch.Views;

public partial class QueueTab : UserControl
{
    public QueueTab() => InitializeComponent();

    /// <summary>
    /// 点表头排序。列和排序键的对应关系靠 x:Name —— 用表头文案匹配会被语言切换打断。
    /// </summary>
    private void QueueHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column == null) return;
        if (DataContext is not MainViewModel vm) return;

        // 表头行末尾还有一个"填充表头"，它的 Column 是 null，上面已经挡掉
        var key = header.Column switch
        {
            var c when ReferenceEquals(c, ColTitle) => QueueSortKey.Title,
            var c when ReferenceEquals(c, ColDuration) => QueueSortKey.Duration,
            var c when ReferenceEquals(c, ColSize) => QueueSortKey.Size,
            var c when ReferenceEquals(c, ColProgress) => QueueSortKey.Progress,
            var c when ReferenceEquals(c, ColStatus) => QueueSortKey.Status,
            var c when ReferenceEquals(c, ColDownloadTime) => QueueSortKey.DownloadTime,
            _ => (QueueSortKey?)null
        };

        if (key.HasValue) vm.SortQueueBy(key.Value);
    }

    /// <summary>SelectedItems 不能直接绑定，转给 ViewModel 算"能删几个文件"。</summary>
    private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list && DataContext is MainViewModel vm)
            vm.UpdateQueueSelection(list.SelectedItems.OfType<DownloadItem>().ToList());
    }

    /// <summary>批量删除本地文件：删文件不可撤销，先确认。</summary>
    private void DeleteFiles_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var count = vm.DeletableSelectionCount;
        if (count == 0) return;

        var result = MessageBox.Show(
            string.Format(LanguageService.GetString("Confirm_DeleteFiles"), count),
            LanguageService.GetString("Confirm_DeleteFiles_Title"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
            vm.DeleteSelectedFilesCommand.Execute(null);
    }

    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView lv || lv.SelectedItem is not DownloadItem item) return;
        if (!item.IsCompleted || string.IsNullOrWhiteSpace(item.OutputPath)) return;
        if (!System.IO.File.Exists(item.OutputPath)) return;

        var vm = DataContext as ViewModels.MainViewModel;
        bool useBuiltIn = vm?.Settings.UseBuiltInPlayer == true;

        if (useBuiltIn)
        {
            // 把设置实例传进去：播放器改音量时能写回 PlayerVolume
            var player = new VideoPlayerWindow(vm?.Settings);
            player.Play(item.OutputPath);
            player.Show();
        }
        else
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = item.OutputPath,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}

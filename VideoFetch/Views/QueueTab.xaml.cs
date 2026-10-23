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
    private bool _restoringSelection;
    private bool _hooked;

    public QueueTab()
    {
        InitializeComponent();

        // RefreshQueueStats 会调 QueueItemsView.Refresh()（发 Reset），ListView 的
        // 选中会因此被清空 —— 多选删文件的按钮就会"闪一下没了"。记住 VM 侧的
        // 选中项，Reset 之后补回去。
        // 用 DataContextChanged 而不是 Loaded：离屏布局（测试）时不会触发 Loaded，
        // 真实运行时 DataContext 也是在 XAML 里赋值的，两条路径都能挂上。
        DataContextChanged += (_, _) => HookQueueView();
        HookQueueView();
    }

    private void HookQueueView()
    {
        if (_hooked || DataContext is not MainViewModel vm) return;
        _hooked = true;

        if (vm.QueueItemsView is System.Collections.Specialized.INotifyCollectionChanged changed)
            changed.CollectionChanged += (_, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                    RestoreSelection();
            };
    }

    private void RestoreSelection()
    {
        if (_restoringSelection || DataContext is not MainViewModel vm) return;
        if (QueueList == null) return;

        var wanted = vm.QueueSelection.Where(i => QueueList.Items.Contains(i)).ToList();
        if (wanted.Count == 0) return;

        _restoringSelection = true;
        try
        {
            QueueList.SelectedItems.Clear();
            foreach (var item in wanted) QueueList.SelectedItems.Add(item);
        }
        catch (InvalidOperationException)
        {
            // 视图正在刷新时改动选中项可能被拒；下一次 Refresh 会再补
        }
        finally
        {
            _restoringSelection = false;
        }
    }

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
        if (_restoringSelection) return;   // 自己补选产生的变化不要回写
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
        // 双击表头/空白/滚动条不该当成「打开这一行」
        if (!RowHitTest.IsRow(e.OriginalSource)) return;

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

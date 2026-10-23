import pathlib

# ── 1) 三个双击处理：只处理来自行的双击 ──
for path, method, second in [
    ("VideoFetch/Views/SearchTab.xaml.cs",
     "private void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)",
     "        if (sender is not ListView lv || lv.SelectedItem is not SearchResult result) return;"),
    ("VideoFetch/Views/CategoryTab.xaml.cs",
     "private void CategoryResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)",
     "        if (sender is not ListView lv || lv.SelectedItem is not SearchResult result) return;"),
    ("VideoFetch/Views/QueueTab.xaml.cs",
     "private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)",
     "        if (sender is not ListView lv || lv.SelectedItem is not DownloadItem item) return;"),
]:
    p = pathlib.Path(path)
    s = p.read_text(encoding="utf-8")
    assert method in s and second in s, path
    s = s.replace(method + "\n    {\n" + second,
                  method + "\n    {\n"
                  "        // 双击表头/空白/滚动条不该当成"打开这一行"\n"
                  "        if (!RowHitTest.IsRow(e.OriginalSource)) return;\n\n"
                  + second, 1)
    p.write_text(s, encoding="utf-8")
    print("hit test:", path)

# ── 2) QueueTab：刷新视图后恢复多选 ──
p = pathlib.Path("VideoFetch/Views/QueueTab.xaml")
s = p.read_text(encoding="utf-8")
old = """            <ListView ItemsSource="{Binding QueueItemsView}\""""
new = """            <ListView x:Name="QueueList" ItemsSource="{Binding QueueItemsView}\""""
assert old in s
p.write_text(s.replace(old, new, 1), encoding="utf-8")
print("QueueTab.xaml named list")

p = pathlib.Path("VideoFetch/Views/QueueTab.xaml.cs")
s = p.read_text(encoding="utf-8")

old2 = """    public QueueTab() => InitializeComponent();"""
new2 = """    private bool _restoringSelection;

    public QueueTab()
    {
        InitializeComponent();

        // RefreshQueueStats 会调 QueueItemsView.Refresh()（发 Reset），
        // ListView 的选中会因此被清空 —— 多选删文件的按钮就"闪一下没了"。
        // 这里记住 VM 侧的选中项，Reset 之后补回去。
        Loaded += (_, _) => HookQueueView();
    }

    private bool _hooked;

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
            // 视图正在刷新时改动选中项可能被拒，下一次 Refresh 会再补
        }
        finally
        {
            _restoringSelection = false;
        }
    }"""
assert old2 in s
s = s.replace(old2, new2, 1)

old3 = """    private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list && DataContext is MainViewModel vm)
            vm.UpdateQueueSelection(list.SelectedItems.OfType<DownloadItem>().ToList());
    }"""
new3 = """    private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection) return;   // 自己补选产生的变化不要回写
        if (sender is ListView list && DataContext is MainViewModel vm)
            vm.UpdateQueueSelection(list.SelectedItems.OfType<DownloadItem>().ToList());
    }"""
assert old3 in s
s = s.replace(old3, new3, 1)
p.write_text(s, encoding="utf-8")
print("QueueTab.cs selection restore")

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoFetch.Models;
using VideoFetch.Services;

namespace VideoFetch.ViewModels;

/// <summary>
/// Main window ViewModel.
/// Handles: URL input -> fetch info -> quality select -> enqueue download.
/// Plus: Search videos by keyword, browse by category.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly VideoInfoService _infoService = new();
    private readonly SettingsService _settingsService = new();
    private readonly DownloadRecordService _recordService = new();
    private DownloadQueueService _queue;

    // ── URL input ──────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    private string _urlInput = string.Empty;

    [ObservableProperty]
    private bool _isFetching;

    [ObservableProperty]
    private string _fetchStatusMessage = string.Empty;

    // ── Fetched video info ─────────────────────────────────────────────────
    [ObservableProperty]
    private VideoInfo? _videoInfo;

    [ObservableProperty]
    private bool _hasVideoInfo;

    [ObservableProperty]
    private string _thumbnailUrl = string.Empty;

    [ObservableProperty]
    private ObservableCollection<QualityOption> _availableQualities = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private QualityOption? _selectedQuality;

    // ── Queue / progress ───────────────────────────────────────────────────
    public ObservableCollection<DownloadItem> DownloadItems => _queue.Items;

    // ── App info ───────────────────────────────────────────────────────────
    public string AppVersion { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";

    /// <summary>
    /// Tab indices of MainWindow's TabControl:
    /// 0=Search, 1=Category, 2=Queue, 3=Settings.
    /// </summary>
    public const int TabSearch = 0;
    public const int TabCategory = 1;
    public const int TabQueue = 2;

    // ── Settings ───────────────────────────────────────────────────────────
    [ObservableProperty]
    private AppSettings _settings;

    [ObservableProperty]
    private int _selectedTab = 0;

    // ══════════════════════════════════════════════════════════
    //  SEARCH & CATEGORY PROPERTIES
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Search query text
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _searchQuery = string.Empty;

    /// <summary>
    /// Currently selected search site (for the site combobox)
    /// </summary>
    [ObservableProperty]
    private SiteType _searchSite = SiteType.PornHub;

    /// <summary>
    /// Search results collection
    /// </summary>
    public ObservableCollection<SearchResult> SearchResults { get; } = new();

    /// <summary>
    /// True when search results list is non-empty (drives ScrollViewer visibility)
    /// </summary>
    [ObservableProperty]
    private bool _hasSearchResults;

    /// <summary>
    /// Count of currently selected (checked) search results
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BatchDownloadCommand))]
    private int _selectedResultCount;

    /// <summary>
    /// Formatted text for batch download button (e.g., "⬇ 批量下载选中项 (3)")
    /// </summary>
    [ObservableProperty]
    private string _batchDownloadText = string.Empty;

    /// <summary>
    /// Selected search result in the list
    /// </summary>
    [ObservableProperty]
    private SearchResult? _selectedSearchResult;

    /// <summary>
    /// Whether a search is in progress
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _isSearching;

    /// <summary>
    /// Status message for search operations
    /// </summary>
    [ObservableProperty]
    private string _searchStatusMessage = string.Empty;

    /// <summary>
    /// Status message for settings save feedback (auto-clears after 2.5s)
    /// </summary>
    [ObservableProperty]
    private string _settingsSavedMessage = string.Empty;

    /// <summary>
    /// Available sites for search dropdown
    /// </summary>
    public IReadOnlyList<SiteType> SupportedSites { get; } =
        new[] { SiteType.PinSe, SiteType.XVideos, SiteType.XNxx, SiteType.PornHub };

    /// <summary>
    /// Current page number for pagination
    /// </summary>
    [ObservableProperty]
    private int _currentPage = 1;

    /// <summary>
    /// Whether more pages are available (for "Load More" button)
    /// </summary>
    [ObservableProperty]
    private bool _hasMoreResults;

    /// <summary>
    /// Whether to include already-downloaded items in batch download.
    /// Default: false (skip downloaded, like Xunlei dedup).
    /// When true: force re-download all selected items.
    /// </summary>
    [ObservableProperty]
    private bool _includeAlreadyDownloaded;

    /// <summary>
    /// 下载队列是否为空（用于显示/隐藏提示文字）
    /// </summary>
    public bool IsQueueEmpty => DownloadItems.Count == 0;

    /// <summary>
    /// Items whose PropertyChanged we subscribed to (for IsSelected tracking).
    /// Needed because Clear() raises Reset with OldItems == null — without this
    /// list the subscriptions would leak on every search.
    /// </summary>
    private readonly HashSet<SearchResult> _subscribedResults = new();

    public MainViewModel()
    {
        _settings = _settingsService.Load();
        _queue = new DownloadQueueService(_settings, _recordService);

        // 应用代理设置（解析器/下载引擎共享同一个 handler）
        HttpClientFactory.Configure(EffectiveProxyUrl());

        // Keep HasSearchResults in sync with SearchResults collection
        // Also update 1-based indices for display
        SearchResults.CollectionChanged += SearchResults_CollectionChanged;

        // Category results use the same selection/numbering plumbing as search results
        CategoryResults.CollectionChanged += CategoryResults_CollectionChanged;

        // 下载页：迅雷式分类视图（全部/下载中/已完成/失败/已删除）
        QueueItemsView = CollectionViewSource.GetDefaultView(_queue.Items);
        QueueItemsView.Filter = FilterDownloadItem;

        // Keep IsQueueEmpty in sync + track item state changes for the filter view
        DownloadItems.CollectionChanged += DownloadItems_CollectionChanged;
        // 订阅历史记录项的属性变化并计算初始计数
        DownloadItems_CollectionChanged(_queue.Items,
            new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }

    // ══════════════════════════════════════════════════════════
    //  QUEUE FILTER (迅雷式分类)
    // ══════════════════════════════════════════════════════════

    /// <summary>Filtered view of the download queue shown in the queue tab.</summary>
    public ICollectionView QueueItemsView { get; }

    /// <summary>Current category selected on the queue page.</summary>
    [ObservableProperty]
    private QueueFilter _queueFilter = QueueFilter.All;

    [ObservableProperty]
    private string _filterAllText = "全部 (0)";

    [ObservableProperty]
    private string _filterDownloadingText = "下载中 (0)";

    [ObservableProperty]
    private string _filterCompletedText = "已完成 (0)";

    [ObservableProperty]
    private string _filterFailedText = "失败 (0)";

    [ObservableProperty]
    private string _filterDeletedText = "已删除 (0)";

    /// <summary>Number of items in the trash (drives 清空回收站 visibility).</summary>
    [ObservableProperty]
    private int _trashCount;

    /// <summary>Empty-state hint text, follows the selected category.</summary>
    [ObservableProperty]
    private string _queueEmptyText = "暂无下载任务";

    [ObservableProperty]
    private bool _isCurrentViewEmpty = true;

    partial void OnQueueFilterChanged(QueueFilter value) => RefreshQueueStats();

    private static bool IsTrashItem(DownloadItem i)
        => !i.IsActive && (i.IsDeleted || i.IsFileMissing);

    private bool FilterDownloadItem(object o)
    {
        if (o is not DownloadItem i) return false;
        return QueueFilter switch
        {
            QueueFilter.Downloading => i.IsActive,
            QueueFilter.Completed => !i.IsActive && i.IsCompleted && !IsTrashItem(i),
            QueueFilter.Failed => !i.IsActive && i.HasFailed && !i.IsCompleted && !IsTrashItem(i),
            QueueFilter.Deleted => IsTrashItem(i),
            _ => !IsTrashItem(i) // 全部：隐藏回收站内容
        };
    }

    private void DownloadItems_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            foreach (DownloadItem i in _queue.Items) i.PropertyChanged += QueueItem_PropertyChanged;
        }
        else
        {
            if (e.NewItems != null)
                foreach (DownloadItem i in e.NewItems) i.PropertyChanged += QueueItem_PropertyChanged;
            if (e.OldItems != null)
                foreach (DownloadItem i in e.OldItems) i.PropertyChanged -= QueueItem_PropertyChanged;
        }

        OnPropertyChanged(nameof(IsQueueEmpty));
        RefreshQueueStats();
    }

    /// <summary>
    /// Only state transitions matter for the filter/counters — ignore the
    /// high-frequency Progress/Speed updates.
    /// </summary>
    private void QueueItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItem.Status)
            or nameof(DownloadItem.IsCompleted)
            or nameof(DownloadItem.HasFailed)
            or nameof(DownloadItem.IsDeleted)
            or nameof(DownloadItem.IsFileMissing))
        {
            RefreshQueueStats();
        }
    }

    private void RefreshQueueStats()
    {
        QueueItemsView.Refresh();

        int active = 0, completed = 0, failed = 0, trash = 0;
        foreach (var i in _queue.Items)
        {
            if (i.IsActive) active++;
            else if (i.IsDeleted || i.IsFileMissing) trash++;
            else if (i.IsCompleted) completed++;
            else if (i.HasFailed) failed++;
        }
        int all = _queue.Items.Count - trash;

        FilterAllText = string.Format(LanguageService.GetString("Queue_Filter_All"), all);
        FilterDownloadingText = string.Format(LanguageService.GetString("Queue_Filter_Downloading"), active);
        FilterCompletedText = string.Format(LanguageService.GetString("Queue_Filter_Completed"), completed);
        FilterFailedText = string.Format(LanguageService.GetString("Queue_Filter_Failed"), failed);
        FilterDeletedText = string.Format(LanguageService.GetString("Queue_Filter_Deleted"), trash);
        TrashCount = trash;

        QueueEmptyText = QueueFilter switch
        {
            QueueFilter.Downloading => LanguageService.GetString("Queue_Empty_Active"),
            QueueFilter.Completed => LanguageService.GetString("Queue_Empty_Completed"),
            QueueFilter.Failed => LanguageService.GetString("Queue_Empty_Failed"),
            QueueFilter.Deleted => LanguageService.GetString("Queue_Empty_Trash"),
            _ => LanguageService.GetString("Queue_Empty")
        };
        IsCurrentViewEmpty = QueueItemsView.IsEmpty;
    }

    /// <summary>删除任务（软删除 → 移入回收站，文件与去重记录保留）。</summary>
    [RelayCommand]
    private void DeleteTask(DownloadItem? item)
    {
        if (item == null || item.IsActive) return;
        item.IsDeleted = true;
        item.Status = LanguageService.GetString("Queue_Status_Deleted");
    }

    /// <summary>从回收站恢复任务。</summary>
    [RelayCommand]
    private void RestoreTask(DownloadItem? item)
    {
        if (item == null || !item.IsDeleted) return;
        item.IsDeleted = false;
        if (File.Exists(item.OutputPath) && item.IsCompleted)
            item.Status = "已完成";
        // 文件已经不在的任务恢复后仍会留在回收站（IsFileMissing）
        else if (!File.Exists(item.OutputPath))
            item.IsFileMissing = true;
    }

    /// <summary>彻底删除：移出列表；文件已丢失时一并清掉去重记录。</summary>
    [RelayCommand]
    private void PurgeTask(DownloadItem? item)
    {
        if (item == null || item.IsActive) return;
        PurgeItems(new[] { item });
    }

    /// <summary>清空回收站。</summary>
    [RelayCommand]
    private void EmptyTrash()
        => PurgeItems(_queue.Items.Where(IsTrashItem).ToList());

    private void PurgeItems(IList<DownloadItem> items)
    {
        foreach (var item in items)
        {
            // 文件还在磁盘上 → 保留记录（去重仍有效）；文件已丢失 → 记录没意义，清掉
            if (!File.Exists(item.OutputPath))
                _recordService.DeleteRecord(item.Url);
        }

        App.Current.Dispatcher.Invoke(() =>
        {
            foreach (var item in items) _queue.Items.Remove(item);
        });

        RefreshSearchMarks();
        RefreshQueueStats();
    }

    /// <summary>同步刷新搜索/分类结果上的"已下载"标记（记录变动后调用）。</summary>
    private void RefreshSearchMarks()
    {
        foreach (var list in new[] { SearchResults, CategoryResults })
        {
            foreach (var r in list)
            {
                var record = _recordService.GetByUrl(r.Url);
                r.IsAlreadyDownloaded = record != null && record.FileExists;
                r.LocalFilePath = record?.FilePath ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// Handles items being added/removed from SearchResults.
    /// Subscribes to PropertyChanged on each new item (for IsSelected checkbox tracking).
    /// </summary>
    private void SearchResults_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                    foreach (SearchResult item in e.NewItems)
                        if (_subscribedResults.Add(item))
                            item.PropertyChanged += SearchResult_PropertyChanged;
                break;

            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
                if (e.OldItems != null)
                    foreach (SearchResult item in e.OldItems)
                        if (_subscribedResults.Remove(item))
                            item.PropertyChanged -= SearchResult_PropertyChanged;
                break;

            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                // Clear() gives us no OldItems — unsubscribe via our own bookkeeping
                foreach (var item in _subscribedResults)
                    item.PropertyChanged -= SearchResult_PropertyChanged;
                _subscribedResults.Clear();
                break;
        }

        HasSearchResults = SearchResults.Count > 0;
        UpdateSelectedCount();
        UpdateIndices(SearchResults);
    }

    /// <summary>
    /// Handles items being added/removed from CategoryResults (same plumbing as SearchResults).
    /// </summary>
    private void CategoryResults_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                    foreach (SearchResult item in e.NewItems)
                        if (_subscribedCategoryResults.Add(item))
                            item.PropertyChanged += SearchResult_PropertyChanged;
                break;

            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
                if (e.OldItems != null)
                    foreach (SearchResult item in e.OldItems)
                        if (_subscribedCategoryResults.Remove(item))
                            item.PropertyChanged -= SearchResult_PropertyChanged;
                break;

            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                foreach (var item in _subscribedCategoryResults)
                    item.PropertyChanged -= SearchResult_PropertyChanged;
                _subscribedCategoryResults.Clear();
                break;
        }

        HasCategoryResults = CategoryResults.Count > 0;
        UpdateCategorySelectedCount();
        UpdateIndices(CategoryResults);
    }

    /// <summary>
    /// Recalculate the category batch download count + button text.
    /// </summary>
    private void UpdateCategorySelectedCount()
    {
        SelectedCategoryResultCount = CategoryResults.Count(r => r.IsSelected);
        BatchCategoryDownloadText = string.Format(
            LanguageService.GetString("Btn_BatchDownload"), SelectedCategoryResultCount);
    }

    /// <summary>
    /// Mark already-downloaded items, add to the target list, and start
    /// background thumbnail loading. Shared by search and category browsing.
    /// </summary>
    private async Task PrepareResultsAsync(List<SearchResult> results, ObservableCollection<SearchResult> target)
    {
        foreach (var r in results)
        {
            var record = _recordService.GetByUrl(r.Url);
            r.IsAlreadyDownloaded = record != null && record.FileExists;
            r.LocalFilePath = record?.FilePath ?? string.Empty;
            target.Add(r);
        }

        if (results.Count > 0)
            _ = LoadThumbnailsAsync(results);
    }

    /// <summary>
    /// Load small thumbnails in the background (max 6 concurrent) and assign
    /// them to the results. Failures are ignored — the placeholder stays.
    /// </summary>
    private static async Task LoadThumbnailsAsync(IEnumerable<SearchResult> items)
    {
        var targets = items
            .Where(i => i.ThumbnailImage == null && !string.IsNullOrWhiteSpace(i.ThumbnailUrl))
            .ToList();
        if (targets.Count == 0) return;

        using var gate = new SemaphoreSlim(6);
        var tasks = targets.Select(async item =>
        {
            try
            {
                await gate.WaitAsync();
                try
                {
                    var img = await ThumbnailLoader.LoadAsync(item.ThumbnailUrl);
                    if (img != null)
                        _ = App.Current.Dispatcher.BeginInvoke(() => item.ThumbnailImage = img);
                }
                finally { gate.Release(); }
            }
            catch { /* ignore single thumbnail failures */ }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// When a SearchResult's IsSelected property changes (user clicked checkbox),
    /// recalculate both collections' batch download counts.
    /// </summary>
    private void SearchResult_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchResult.IsSelected))
        {
            UpdateSelectedCount();
            UpdateCategorySelectedCount();
        }
    }

    /// <summary>
    /// Update 1-based Index property on all results (for row numbering)
    /// </summary>
    private static void UpdateIndices(ObservableCollection<SearchResult> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Index = i + 1;
        }
    }

    /// <summary>
    /// Recalculate selected result count (called when items are added/removed or IsSelected changes)
    /// </summary>
    private void UpdateSelectedCount()
    {
        SelectedResultCount = SearchResults.Count(r => r.IsSelected);
        // Update batch download button text with formatted count
        var format = LanguageService.GetString("Btn_BatchDownload");
        BatchDownloadText = string.Format(format, SelectedResultCount);
    }

    // ══════════════════════════════════════════════════════════
    //  COMMANDS: URL / DOWNLOAD
    // ══════════════════════════════════════════════════════════

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchAsync()
    {
        var url = UrlInput.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        IsFetching = true;
        FetchStatusMessage = "Fetching video info...";
        HasVideoInfo = false;
        VideoInfo = null;
        AvailableQualities.Clear();
        SelectedQuality = null;

        try
        {
            if (!_infoService.IsSupported(url))
            {
                FetchStatusMessage = "This URL is not supported. Supported: PornHub, xvideos, xnxx, 91pinse";
                return;
            }

            var info = await _infoService.FetchAsync(url);
            VideoInfo = info;
            HasVideoInfo = true;
            ThumbnailUrl = info.ThumbnailUrl;
            FetchStatusMessage = string.Empty;

            foreach (var q in info.Qualities)
                AvailableQualities.Add(q);

            SelectedQuality = AvailableQualities.FirstOrDefault();
        }
        catch (Exception ex)
        {
            FetchStatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsFetching = false;
        }
    }

    private bool CanFetch() => !string.IsNullOrWhiteSpace(UrlInput) && !IsFetching;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private void Download()
    {
        if (VideoInfo == null || SelectedQuality == null) return;

        var url = VideoInfo.Url;

        // 去重检查：如果已下载过且本地文件还存在，阻止下载
        if (_recordService.IsDownloaded(url))
        {
            FetchStatusMessage = "⚠️ 该视频已下载过，文件已存在。如需重新下载请先删除本地文件。";
            return;
        }

        // 内存去重：同一URL正在排队/下载中
        if (_queue.Items.Any(i => string.Equals(i.Url, url, StringComparison.OrdinalIgnoreCase) && i.IsActive))
        {
            FetchStatusMessage = "⚠️ 该视频已在下载队列中";
            return;
        }

        _queue.Enqueue(VideoInfo, SelectedQuality.Value);

        SelectedTab = TabQueue; // Switch to queue tab

        FetchStatusMessage = $"Added to queue: {VideoInfo.Title}";
        VideoInfo = null;
        HasVideoInfo = false;
        AvailableQualities.Clear();
    }

    private bool CanDownload() => VideoInfo != null && SelectedQuality != null && HasVideoInfo;

    [RelayCommand]
    private void CancelDownload(DownloadItem? item)
    {
        if (item != null) _queue.Cancel(item);
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        try
        {
            var dir = Settings.OutputDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception ex)
        {
            FetchStatusMessage = $"无法打开文件夹: {ex.Message}";
        }
    }

    [RelayCommand]
    private void BrowseOutputFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载保存路径",
            InitialDirectory = Settings.OutputDirectory
        };
        if (dialog.ShowDialog() == true)
        {
            Settings.OutputDirectory = dialog.FolderName;
            OnPropertyChanged(nameof(Settings));
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        _settingsService.Save(Settings);
        // Apply new settings to the long-lived queue service (never rebuild it:
        // rebuilding orphans in-flight downloads and breaks the Items binding)
        _queue.ApplySettings();
        HttpClientFactory.Configure(EffectiveProxyUrl());

        // Show save success message (auto-clear after 2.5 seconds)
        SettingsSavedMessage = "✅ 设置保存成功！";
        await Task.Delay(2500);
        SettingsSavedMessage = string.Empty;
    }

    /// <summary>
    /// The proxy actually in effect: the configured address only when the
    /// "启用代理" switch is on, so the address survives toggling.
    /// </summary>
    private string EffectiveProxyUrl()
        => Settings.ProxyEnabled ? Settings.ProxyUrl : string.Empty;

    [RelayCommand]
    private void PasteUrl()
    {
        var clip = System.Windows.Clipboard.GetText();
        if (!string.IsNullOrEmpty(clip))
        {
            UrlInput = clip.Trim();
            _ = FetchAsync();
        }
    }

    partial void OnUrlInputChanged(string value)
    {
        FetchStatusMessage = string.Empty;
    }

    /// <summary>
    /// 删除已下载视频的本地文件（支持强制删除被占用的文件）
    /// </summary>
    [RelayCommand]
    private void DeleteLocalFile(DownloadItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.OutputPath)) return;
        try
        {
            if (!System.IO.File.Exists(item.OutputPath))
            {
                _recordService.DeleteRecord(item.Url);
                item.Status = "文件已不存在";
                item.IsCompleted = false;
                item.IsFileMissing = true;
                return;
            }

            // 尝试1：普通删除
            try { System.IO.File.Delete(item.OutputPath); }
            catch (System.IO.IOException)
            {
                // 尝试2：移除只读属性后删除
                try
                {
                    System.IO.File.SetAttributes(item.OutputPath, System.IO.FileAttributes.Normal);
                    System.IO.File.Delete(item.OutputPath);
                }
                catch
                {
                    // 尝试3：标记为重启后删除（强制解决文件占用）
                    var tempName = item.OutputPath + ".delete_" + DateTime.Now.Ticks;
                    System.IO.File.Move(item.OutputPath, tempName);
                    try { System.IO.File.Delete(tempName); }
                    catch
                    {
                        // MoveFileEx: MOVEFILE_DELAY_UNTIL_REBOOT = 4
                        // 文件将在下次重启时被系统删除
                        try { PInvoke.MoveFileEx(tempName, null, 4); }
                        catch
                        {
                            // 最后的方案：覆盖为空文件后将原文件重命名标记
                            using (var fs = new System.IO.FileStream(tempName, System.IO.FileMode.Create))
                                fs.SetLength(0);
                            try { PInvoke.MoveFileEx(tempName, null, 4); } catch { }
                        }
                        item.Status = "文件将在重启后删除";
                        _recordService.DeleteRecord(item.Url);
                        item.IsCompleted = false;
                        item.IsFileMissing = true;
                        return;
                    }
                }
            }

            // 成功删除
            _recordService.DeleteRecord(item.Url);
            item.Status = "本地文件已删除";
            item.IsCompleted = false;
            item.IsFileMissing = true;
        }
        catch (Exception ex)
        {
            item.Status = $"删除失败: {ex.Message}";
        }
    }

    /// <summary>
    /// Win32 P/Invoke helpers for file operations
    /// </summary>
    private static class PInvoke
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern bool MoveFileEx(string? lpExistingFileName, string? lpNewFileName, int dwFlags);
    }

    // ══════════════════════════════════════════════════════════
    //  COMMANDS: SEARCH
    // ══════════════════════════════════════════════════════════

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        var query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        IsSearching = true;
        SearchStatusMessage = $"Searching {SearchSite} for \"{query}\"...";
        SearchResults.Clear();
        SelectedSearchResult = null;
        CurrentPage = 1;

        try
        {
            var results = await _infoService.SearchAsync(SearchSite, query, page: 1);
            await PrepareResultsAsync(results, SearchResults);

            HasMoreResults = results.Count >= 20; // assume more if we got a full page
            SearchStatusMessage = results.Count > 0
                ? $"Found {results.Count} results"
                : "No results found. Try different keywords.";
        }
        catch (Exception ex)
        {
            SearchStatusMessage = $"Search error: {ex.Message}";
            HasMoreResults = false;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private bool CanSearch() => !string.IsNullOrWhiteSpace(SearchQuery) && !IsSearching;

    /// <summary>
    /// Load next page of search results (append mode).
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreSearchResultsAsync()
    {
        if (!HasMoreResults || IsSearching) return;
        var nextPage = CurrentPage + 1;
        IsSearching = true;
        SearchStatusMessage = $"Loading page {nextPage}...";

        try
        {
            var results = await _infoService.SearchAsync(SearchSite, SearchQuery.Trim(), page: nextPage);
            await PrepareResultsAsync(results, SearchResults);
            CurrentPage = nextPage;
            HasMoreResults = results.Count >= 20;
            SearchStatusMessage = $"{SearchResults.Count} total results loaded";
        }
        catch (Exception ex)
        {
            SearchStatusMessage = $"Error loading more: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  COMMANDS: SELECT SEARCH RESULT -> DIRECT DOWNLOAD
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Route a result-related status message to the tab the result lives in
    /// (search tab vs category tab).
    /// </summary>
    private void SetResultStatus(SearchResult? result, string message)
    {
        if (result != null && CategoryResults.Contains(result))
            CategoryStatusMessage = message;
        else
            SearchStatusMessage = message;
    }

    /// <summary>
    /// When user double-clicks or clicks Select on a search result:
    /// 1. Fetch full video info from the URL
    /// 2. Auto-select best quality (first in list = highest)
    /// 3. Enqueue for download
    /// </summary>
    [RelayCommand]
    private async Task DownloadSearchResultAsync(SearchResult? result)
    {
        var target = result ?? SelectedSearchResult;
        var url = target?.Url;
        if (url is not string s || string.IsNullOrWhiteSpace(s)) return;

        // 去重检查1：已下载且文件存在
        if (_recordService.IsDownloaded(s))
        {
            SetResultStatus(target, "⚠️ 该视频已下载过，文件已存在。如需重新下载请先删除本地文件。");
            return;
        }

        // 去重检查2：同一URL在下载队列中
        if (_queue.Items.Any(i => string.Equals(i.Url, s, StringComparison.OrdinalIgnoreCase) && i.IsActive))
        {
            SetResultStatus(target, "⚠️ 该视频已在下载队列中");
            return;
        }

        // Show status: fetching info...
        SetResultStatus(target, $"Preparing download: {target?.Title ?? "loading..."}");

        try
        {
            if (!_infoService.IsSupported(s))
            {
                SetResultStatus(target, $"Unsupported site: {s}");
                return;
            }

            var info = await _infoService.FetchAsync(s);
            if (info.Qualities.Count == 0)
            {
                SetResultStatus(target, "Could not get video info or no qualities available.");
                return;
            }

            // Auto-select best quality (first item = highest resolution)
            var bestQuality = info.Qualities[0];

            // Enqueue download (recordService will be called by DownloadQueueService on completion)
            _queue.Enqueue(info, bestQuality.Value);

            SetResultStatus(target, $"\u2705 Download started: {info.Title} ({bestQuality.Label})");
        }
        catch (Exception ex)
        {
            SetResultStatus(target, $"Error: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  COMMANDS: BATCH SELECTION & DOWNLOAD
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Select all search results
    /// </summary>
    [RelayCommand]
    private void SelectAllResults()
    {
        foreach (var item in SearchResults)
            item.IsSelected = true;
        UpdateSelectedCount();
    }

    /// <summary>
    /// Invert selection of all search results
    /// </summary>
    [RelayCommand]
    private void InvertSelection()
    {
        foreach (var item in SearchResults)
            item.IsSelected = !item.IsSelected;
        UpdateSelectedCount();
    }

    /// <summary>
    /// Download all selected search results (auto-select best quality for each).
    /// Skips already-downloaded videos by default (similar to Xunlei's dedup logic).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBatchDownload))]
    private Task BatchDownloadAsync()
        => BatchDownloadCoreAsync(SearchResults, m => SearchStatusMessage = m);

    private bool CanBatchDownload() => SelectedResultCount > 0 && !IsSearching;

    /// <summary>
    /// Download all selected results (auto-select best quality for each).
    /// Skips already-downloaded videos by default (similar to Xunlei's dedup logic).
    /// Shared by the search tab and the category tab.
    /// </summary>
    private async Task BatchDownloadCoreAsync(IReadOnlyList<SearchResult> source, Action<string> setStatus)
    {
        var selected = source.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) return;

        // 过滤掉已下载的视频（类似迅雷去重）
        // 如果用户勾选了"包含已下载项"，则不过滤（强制重新下载）
        // 同时过滤已经在下载队列中的（防止内存重复）
        var toDownload = new List<SearchResult>();
        int skippedCount = 0;
        foreach (var result in selected)
        {
            // 检查1：已下载且文件存在 → 跳过
            if (!IncludeAlreadyDownloaded && _recordService.IsDownloaded(result.Url))
            {
                skippedCount++;
                continue;
            }

            // 检查2：已在下载队列中（排队中/下载中） → 跳过
            if (_queue.Items.Any(i => string.Equals(i.Url, result.Url, StringComparison.OrdinalIgnoreCase) && i.IsActive))
            {
                skippedCount++;
                continue;
            }

            toDownload.Add(result);
        }

        // 如果全部跳过
        if (toDownload.Count == 0)
        {
            setStatus($"⚠️ 已跳过 {skippedCount} 个已下载过的视频 (本地文件存在)");
            return;
        }

        if (skippedCount > 0)
        {
            setStatus($"批量下载: {toDownload.Count} 个视频 (跳过 {skippedCount} 个已下载)");
        }
        else
        {
            setStatus($"Preparing batch download: {toDownload.Count} videos...");
        }

        int successCount = 0;
        int failCount = 0;

        foreach (var result in toDownload)
        {
            try
            {
                if (!_infoService.IsSupported(result.Url)) { failCount++; continue; }

                var info = await _infoService.FetchAsync(result.Url);
                if (info.Qualities.Count == 0) { failCount++; continue; }

                var bestQuality = info.Qualities[0];
                _queue.Enqueue(info, bestQuality.Value);
                successCount++;
            }
            catch { failCount++; }
        }

        setStatus($"\u2705 批量下载完成: 成功 {successCount} 个, 失败 {failCount} 个"
            + (skippedCount > 0 ? $" (跳过 {skippedCount} 个已下载)" : ""));
        SelectedTab = TabQueue; // Switch to queue tab

        // Clear selection after download (counts refresh via PropertyChanged)
        foreach (var item in source)
            item.IsSelected = false;
    }

    // ══════════════════════════════════════════════════════════
    //  CATEGORY BROWSE
    // ══════════════════════════════════════════════════════════

    /// <summary>Site whose categories are shown in the category tab.</summary>
    [ObservableProperty]
    private SiteType _categorySite = SiteType.PinSe;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCategoriesCommand))]
    private bool _isLoadingCategories;

    [ObservableProperty]
    private string _categoryStatusMessage = string.Empty;

    /// <summary>Category tags of the selected site.</summary>
    public ObservableCollection<CategoryItem> Categories { get; } = new();

    [ObservableProperty]
    private CategoryItem? _selectedCategory;

    /// <summary>Videos of the currently browsed category.</summary>
    public ObservableCollection<SearchResult> CategoryResults { get; } = new();

    /// <summary>Tracked subscriptions for CategoryResults items (Reset has no OldItems).</summary>
    private readonly HashSet<SearchResult> _subscribedCategoryResults = new();

    [ObservableProperty]
    private bool _hasCategoryResults;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BatchDownloadCategoryCommand))]
    private int _selectedCategoryResultCount;

    /// <summary>Formatted batch download button text (e.g. "⬇ 批量下载选中项 (3)").</summary>
    [ObservableProperty]
    private string _batchCategoryDownloadText = string.Empty;

    [ObservableProperty]
    private int _currentCategoryPage = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCategoryResultsCommand))]
    private bool _hasMoreCategoryResults;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BatchDownloadCategoryCommand), nameof(LoadMoreCategoryResultsCommand))]
    private bool _isBrowsingCategory;

    /// <summary>The category currently being browsed (for pagination).</summary>
    private CategoryItem? _browsedCategory;

    partial void OnCategorySiteChanged(SiteType value)
    {
        // 切换站点后需要重新加载分类列表
        Categories.Clear();
        SelectedCategory = null;
        _browsedCategory = null;
        CategoryResults.Clear();
        HasMoreCategoryResults = false;
        CategoryStatusMessage = string.Empty;
    }

    /// <summary>
    /// Load the category list of the selected site.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadCategories))]
    private async Task LoadCategoriesAsync()
    {
        IsLoadingCategories = true;
        Categories.Clear();
        CategoryStatusMessage = string.Format(LanguageService.GetString("Cat_Loading"), CategorySite);

        try
        {
            var categories = await _infoService.GetCategoriesAsync(CategorySite);
            foreach (var c in categories)
                Categories.Add(c);

            CategoryStatusMessage = categories.Count > 0
                ? string.Format(LanguageService.GetString("Cat_Loaded"), CategorySite, categories.Count)
                : string.Format(LanguageService.GetString("Cat_NoCategories"), CategorySite);
        }
        catch (Exception ex)
        {
            CategoryStatusMessage = string.Format(
                LanguageService.GetString("Cat_LoadFailed"), ex.Message);
        }
        finally
        {
            IsLoadingCategories = false;
        }
    }

    private bool CanLoadCategories() => !IsLoadingCategories;

    /// <summary>
    /// Browse videos of a category (page 1, replacing previous results).
    /// </summary>
    [RelayCommand]
    private async Task BrowseCategoryAsync(CategoryItem? category)
    {
        if (category == null || IsBrowsingCategory) return;

        SelectedCategory = category;
        _browsedCategory = category;
        CurrentCategoryPage = 1;
        CategoryResults.Clear();
        HasMoreCategoryResults = false;

        await BrowseCategoryPageAsync(category, 1);
    }

    /// <summary>
    /// Append the next page of the currently browsed category.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadMoreCategoryResults))]
    private async Task LoadMoreCategoryResultsAsync()
    {
        if (_browsedCategory == null) return;
        await BrowseCategoryPageAsync(_browsedCategory, CurrentCategoryPage + 1);
    }

    private bool CanLoadMoreCategoryResults()
        => !IsBrowsingCategory && HasMoreCategoryResults && _browsedCategory != null;

    private async Task BrowseCategoryPageAsync(CategoryItem category, int page)
    {
        IsBrowsingCategory = true;
        CategoryStatusMessage = string.Format(
            LanguageService.GetString("Cat_Browsing"), category.Name, page);

        try
        {
            var results = await _infoService.BrowseCategoryAsync(CategorySite, category.Key, page);
            await PrepareResultsAsync(results, CategoryResults);

            CurrentCategoryPage = page;
            HasMoreCategoryResults = results.Count >= 20; // assume more if we got a full page
            CategoryStatusMessage = results.Count > 0
                ? string.Format(LanguageService.GetString("Cat_Browsed"), category.Name, results.Count)
                : LanguageService.GetString("Cat_NoResults");
        }
        catch (Exception ex)
        {
            CategoryStatusMessage = string.Format(
                LanguageService.GetString("Cat_BrowseError"), ex.Message);
            HasMoreCategoryResults = false;
        }
        finally
        {
            IsBrowsingCategory = false;
        }
    }

    /// <summary>Select all category results.</summary>
    [RelayCommand]
    private void SelectAllCategoryResults()
    {
        foreach (var item in CategoryResults)
            item.IsSelected = true;
    }

    /// <summary>Invert selection of category results.</summary>
    [RelayCommand]
    private void InvertCategorySelection()
    {
        foreach (var item in CategoryResults)
            item.IsSelected = !item.IsSelected;
    }

    /// <summary>
    /// Batch download selected category results (shares the dedup logic
    /// with the search tab's batch download).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBatchCategoryDownload))]
    private Task BatchDownloadCategoryAsync()
        => BatchDownloadCoreAsync(CategoryResults, m => CategoryStatusMessage = m);

    private bool CanBatchCategoryDownload() => SelectedCategoryResultCount > 0 && !IsBrowsingCategory;
}

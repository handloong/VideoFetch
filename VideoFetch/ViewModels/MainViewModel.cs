using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
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
    public List<SiteType> SupportedSites => new() { SiteType.PornHub, SiteType.XVideos, SiteType.XNxx };

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

    public MainViewModel()
    {
        _settings = _settingsService.Load();
        _recordService = new DownloadRecordService();
        _queue = new DownloadQueueService(_settings, _recordService);

        // Keep HasSearchResults in sync with SearchResults collection
        // Also update 1-based indices for display
        SearchResults.CollectionChanged += SearchResults_CollectionChanged;
    }

    /// <summary>
    /// Handles items being added/removed from SearchResults.
    /// Subscribes to PropertyChanged on each new item (for IsSelected checkbox tracking).
    /// </summary>
    private void SearchResults_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Subscribe to new items' PropertyChanged (for IsSelected → batch count)
        if (e.NewItems != null)
        {
            foreach (SearchResult item in e.NewItems)
                item.PropertyChanged += SearchResult_PropertyChanged;
        }

        // Unsubscribe from removed items
        if (e.OldItems != null)
        {
            foreach (SearchResult item in e.OldItems)
                item.PropertyChanged -= SearchResult_PropertyChanged;
        }

        HasSearchResults = SearchResults.Count > 0;
        UpdateSelectedCount();
        UpdateIndices();
    }

    /// <summary>
    /// When a SearchResult's IsSelected property changes (user clicked checkbox),
    /// recalculate the batch download count.
    /// </summary>
    private void SearchResult_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchResult.IsSelected))
        {
            UpdateSelectedCount();
        }
    }

    /// <summary>
    /// Update 1-based Index property on all SearchResults (for row numbering)
    /// </summary>
    private void UpdateIndices()
    {
        for (int i = 0; i < SearchResults.Count; i++)
        {
            SearchResults[i].Index = i + 1;
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
                FetchStatusMessage = "This URL is not supported. Supported: PornHub, xvideos, xnxx";
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
        if (_queue.Items.Any(i => string.Equals(i.Url, url, StringComparison.OrdinalIgnoreCase)
            && !i.IsCompleted && !i.HasFailed))
        {
            FetchStatusMessage = "⚠️ 该视频已在下载队列中";
            return;
        }

        _queue.Enqueue(VideoInfo, SelectedQuality.Value);

        SelectedTab = 1; // Switch to queue tab

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
    private void ClearCompleted() => _queue.RemoveCompleted();

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var dir = Settings.OutputDirectory;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    [RelayCommand]
    private void BrowseOutputFolder()
    {
        // Open current output folder in Explorer so user can see/select path
        var dir = Settings.OutputDirectory;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        _settingsService.Save(Settings);
        // Rebuild queue service with new settings
        _queue = new DownloadQueueService(Settings, _recordService);
        OnPropertyChanged(nameof(DownloadItems));

        // Show save success message (auto-clear after 2.5 seconds)
        SettingsSavedMessage = "✅ 设置保存成功！";
        await Task.Delay(2500);
        SettingsSavedMessage = string.Empty;
    }

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
                        return;
                    }
                }
            }

            // 成功删除
            _recordService.DeleteRecord(item.Url);
            item.Status = "本地文件已删除";
            item.IsCompleted = false;
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
            foreach (var r in results)
            {
                // Check download history and mark if already downloaded
                var record = _recordService.GetByUrl(r.Url);
                r.IsAlreadyDownloaded = record != null && record.FileExists;
                r.LocalFilePath = record?.FilePath ?? string.Empty;
                SearchResults.Add(r);
            }

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
            foreach (var r in results)
            {
                // Check download history for each new result too
                r.IsAlreadyDownloaded = _recordService.IsDownloaded(r.Url);
                SearchResults.Add(r);
            }
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
    /// When user double-clicks or clicks Select on a search result:
    /// 1. Fetch full video info from the URL
    /// 2. Auto-select best quality (first in list = highest)
    /// 3. Enqueue for download
    /// </summary>
    [RelayCommand]
    private async Task DownloadSearchResultAsync(SearchResult? result)
    {
        var url = result?.Url ?? SelectedSearchResult?.Url;
        if (url is not string s || string.IsNullOrWhiteSpace(s)) return;

        // 去重检查1：已下载且文件存在
        if (_recordService.IsDownloaded(s))
        {
            SearchStatusMessage = "⚠️ 该视频已下载过，文件已存在。如需重新下载请先删除本地文件。";
            return;
        }

        // 去重检查2：同一URL在下载队列中
        if (_queue.Items.Any(i => string.Equals(i.Url, s, StringComparison.OrdinalIgnoreCase)
            && !i.IsCompleted && !i.HasFailed))
        {
            SearchStatusMessage = "⚠️ 该视频已在下载队列中";
            return;
        }

        // Show status: fetching info...
        SearchStatusMessage = $"Preparing download: {result?.Title ?? "loading..."}";

        try
        {
            if (!_infoService.IsSupported(s))
            {
                SearchStatusMessage = $"Unsupported site: {s}";
                return;
            }

            var info = await _infoService.FetchAsync(s);
            if (info == null || info.Qualities.Count == 0)
            {
                SearchStatusMessage = "Could not get video info or no qualities available.";
                return;
            }

            // Auto-select best quality (first item = highest resolution)
            var bestQuality = info.Qualities.FirstOrDefault();
            if (bestQuality == null) bestQuality = info.Qualities[0];

            // Enqueue download (recordService will be called by DownloadQueueService on completion)
            _queue.Enqueue(info, bestQuality.Value);

            SearchStatusMessage = $"\u2705 Download started: {info.Title} ({bestQuality.Label})";
        }
        catch (Exception ex)
        {
            SearchStatusMessage = $"Error: {ex.Message}";
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
    private async Task BatchDownloadAsync()
    {
        var selected = SearchResults.Where(r => r.IsSelected).ToList();
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
            if (_queue.Items.Any(i => string.Equals(i.Url, result.Url, StringComparison.OrdinalIgnoreCase)
                && !i.IsCompleted && !i.HasFailed))
            {
                skippedCount++;
                continue;
            }

            toDownload.Add(result);
        }

        // 如果全部跳过
        if (toDownload.Count == 0)
        {
            SearchStatusMessage = $"⚠️ 已跳过 {skippedCount} 个已下载过的视频 (本地文件存在)";
            return;
        }

        if (skippedCount > 0)
        {
            SearchStatusMessage = $"批量下载: {toDownload.Count} 个视频 (跳过 {skippedCount} 个已下载)";
        }
        else
        {
            SearchStatusMessage = $"Preparing batch download: {toDownload.Count} videos...";
        }

        int successCount = 0;
        int failCount = 0;

        foreach (var result in toDownload)
        {
            try
            {
                if (!_infoService.IsSupported(result.Url)) { failCount++; continue; }

                var info = await _infoService.FetchAsync(result.Url);
                if (info == null || info.Qualities.Count == 0) { failCount++; continue; }

                var bestQuality = info.Qualities[0];
                _queue.Enqueue(info, bestQuality.Value);
                successCount++;
            }
            catch { failCount++; }
        }

        SearchStatusMessage = $"\u2705 批量下载完成: 成功 {successCount} 个, 失败 {failCount} 个"
            + (skippedCount > 0 ? $" (跳过 {skippedCount} 个已下载)" : "");
        SelectedTab = 1; // Switch to queue tab

        // Clear selection after download
        foreach (var item in SearchResults)
            item.IsSelected = false;
        UpdateSelectedCount();
    }

    private bool CanBatchDownload() => SelectedResultCount > 0 && !IsSearching;
}

using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using VideoFetch.Models;

namespace VideoFetch.Services;

/// <summary>
/// Manages the download queue - tracks active, queued, and completed downloads.
/// Enforces a max concurrent download limit.
///
/// The service is created once at startup and shared for the app's lifetime.
/// Settings changes are applied via ApplySettings() instead of recreating the
/// service (recreating it would orphan in-flight downloads and break UI bindings).
/// </summary>
public class DownloadQueueService
{
    private readonly AppSettings _settings;
    private readonly DownloadRecordService _recordService;
    private readonly VideoInfoService _infoService;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private int _currentMaxConcurrent;

    public ObservableCollection<DownloadItem> Items { get; } = new();

    public DownloadQueueService(AppSettings settings, DownloadRecordService recordService)
    {
        _settings = settings;
        _recordService = recordService;
        _infoService = new VideoInfoService();
        _currentMaxConcurrent = Math.Max(1, settings.MaxConcurrentDownloads);
        _concurrencySemaphore = new SemaphoreSlim(_currentMaxConcurrent, _currentMaxConcurrent);

        // 启动时加载历史下载记录
        LoadHistory();
    }

    /// <summary>
    /// Apply changed settings without rebuilding the service.
    /// - Proxy: HttpClientFactory.Configure is called by the ViewModel.
    /// - MaxConcurrentDownloads: the semaphore grows immediately; shrinking
    ///   takes effect on app restart (tokens already issued can't be revoked).
    /// - MaxSegmentThreads: picked up automatically per download.
    /// </summary>
    public void ApplySettings()
    {
        var target = Math.Max(1, _settings.MaxConcurrentDownloads);
        if (target > _currentMaxConcurrent)
        {
            _concurrencySemaphore.Release(target - _currentMaxConcurrent);
            _currentMaxConcurrent = target;
        }
    }

    /// <summary>
    /// Load completed download records from SQLite into the queue display.
    /// These are read-only history entries — no re-downloading.
    /// </summary>
    private void LoadHistory()
    {
        foreach (var rec in _recordService.GetAllRecords())
        {
            var fileExists = System.IO.File.Exists(rec.FilePath);
            Items.Add(new DownloadItem
            {
                Title = rec.Title,
                Url = rec.Url,
                OutputPath = rec.FilePath,
                Quality = rec.Quality,
                Progress = 100,
                IsCompleted = fileExists,
                Status = fileExists ? "已完成" : "文件已删除",
                Speed = string.Empty,
                AddedAt = rec.DownloadedAt,
                IsActive = false
            });
        }
    }

    /// <summary>
    /// Add a new download to the queue and start it (subject to concurrency limit).
    /// Returns false if the same URL is already active in the queue (in-memory dedup).
    /// </summary>
    public bool Enqueue(VideoInfo info, string qualityKey)
    {
        // 内存去重：检查同一URL是否已在队列中（排队中/下载中）
        if (Items.Any(i => string.Equals(i.Url, info.Url, StringComparison.OrdinalIgnoreCase) && i.IsActive))
            return false; // 已在队列中，跳过

        var safeTitle = MakeSafeFileName(info.Title);
        var subDir = _settings.CreateSubfolders
            ? Path.Combine(_settings.OutputDirectory, MakeSafeFileName(info.Site.ToString()))
            : _settings.OutputDirectory;

        Directory.CreateDirectory(subDir);

        // HLS 流拼出来的是 MPEG-TS；ffmpeg 可用时会在下载完成后重封装为 .mp4
        var quality = VideoInfoService.ResolveQualityKey(info, qualityKey);
        var ext = quality.Contains("hls", StringComparison.OrdinalIgnoreCase) ? ".ts" : ".mp4";
        var outputPath = EnsureUniquePath(Path.Combine(subDir, safeTitle + ext));

        var item = new DownloadItem
        {
            Title = info.Title,
            Url = info.Url,
            OutputPath = outputPath,
            Quality = qualityKey,
            Site = info.Site,
            Status = "Queued",
            IsActive = true
        };

        App.Current.Dispatcher.Invoke(() => Items.Add(item));
        _ = StartDownloadAsync(item, info, qualityKey);
        return true;
    }

    private async Task StartDownloadAsync(DownloadItem item, VideoInfo info, string qualityKey)
    {
        // Cancel while still queued → WaitAsync throws before the try block below
        try
        {
            await _concurrencySemaphore.WaitAsync(item.CancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            FinishItem(item, "Cancelled");
            return;
        }

        try
        {
            if (item.CancellationSource.Token.IsCancellationRequested)
            {
                FinishItem(item, "Cancelled");
                return;
            }

            item.Status = "Downloading";
            item.IsDownloading = true;

            var engine = new DownloadEngine(_settings.MaxSegmentThreads);
            var streamUrl = await _infoService.ResolveStreamUrlAsync(info, qualityKey,
                item.CancellationSource.Token);

            // Pass the original page URL as Referer (required for PornHub CDN auth)
            var finalPath = await engine.DownloadAsync(
                streamUrl,
                item.OutputPath,
                pageUrl: info.Url,
                onProgress: (pct, speed) =>
                {
                    // BeginInvoke: never block download threads on the UI thread
                    App.Current.Dispatcher.BeginInvoke(() =>
                    {
                        if (pct >= 0) item.Progress = pct;
                        item.Speed = speed;
                        item.Status = pct >= 100 ? "Completed" : "Downloading";
                    });
                },
                ct: item.CancellationSource.Token);

            // HLS remux may have produced a different (mp4) file name
            item.OutputPath = finalPath;
            item.Progress = 100;
            item.Status = "Completed";
            item.IsCompleted = true;
            item.IsActive = false;

            // 下载完成后记录到SQLite（仅在文件保存成功后）
            try
            {
                var fileSize = System.IO.File.Exists(item.OutputPath)
                    ? new System.IO.FileInfo(item.OutputPath).Length : 0L;
                var siteName = item.Site.ToString();
                _recordService.InsertRecord(item.Url, item.Title, siteName,
                    item.Quality, item.OutputPath, fileSize);
            }
            catch { /* 记录失败不影响下载流程 */ }

            if (_settings.AutoOpenFolder)
                System.Diagnostics.Process.Start("explorer.exe",
                    $"/select,\"{item.OutputPath}\"");
        }
        catch (OperationCanceledException)
        {
            FinishItem(item, "Cancelled");
            // Clean up partial file
            try { if (File.Exists(item.OutputPath)) File.Delete(item.OutputPath); } catch { }
        }
        catch (Exception ex)
        {
            item.Status = $"Failed: {ex.Message}";
            item.HasFailed = true;
            item.IsActive = false;
            // Clean up partial file so the dedup check (file exists) isn't fooled
            try { if (File.Exists(item.OutputPath)) File.Delete(item.OutputPath); } catch { }
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    private static void FinishItem(DownloadItem item, string status)
    {
        item.Status = status;
        item.IsActive = false;
    }

    public void Cancel(DownloadItem item)
    {
        if (item == null || item.IsCompleted || item.HasFailed) return;
        item.CancellationSource.Cancel();
        item.Status = "Cancelling...";
        // A queued (not yet started) item never touches the semaphore;
        // mark it inactive immediately so dedup/queue-clear see it as done.
        if (!item.IsDownloading) item.IsActive = false;
    }

    public void CancelAll()
    {
        foreach (var item in Items.Where(i => i.IsActive))
            Cancel(item);
    }

    /// <summary>
    /// 清除所有非活动任务（已完成/失败/已取消/历史记录），并同步清理数据库记录。
    /// 活动任务（排队中或下载中）不受影响。
    /// </summary>
    public void RemoveCompleted()
    {
        // 只保留活动中的任务（排队中/下载中），不再依赖状态字符串匹配
        var toRemove = Items.Where(i => !i.IsActive).ToList();

        // 从数据库删除被清除项的记录
        foreach (var item in toRemove)
            _recordService.DeleteRecord(item.Url);

        App.Current.Dispatcher.Invoke(() =>
        {
            foreach (var item in toRemove) Items.Remove(item);
        });

        // 如果队列为空，清空整个数据库
        if (Items.Count == 0)
            _recordService.ClearAll();
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Length > 120 ? safe[..120] : safe;
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }
}

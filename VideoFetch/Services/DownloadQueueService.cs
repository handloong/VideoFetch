using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using VideoFetch.Models;

namespace VideoFetch.Services;

/// <summary>
/// Manages the download queue - tracks active, queued, and completed downloads.
/// Enforces a max concurrent download limit.
/// </summary>
public class DownloadQueueService
{
    private readonly int _maxConcurrent;
    private readonly DownloadEngine _engine;
    private readonly VideoInfoService _infoService;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly AppSettings _settings;
    private readonly DownloadRecordService _recordService;

    public ObservableCollection<DownloadItem> Items { get; } = new();

    public DownloadQueueService(AppSettings settings, DownloadRecordService recordService)
    {
        _settings = settings;
        _recordService = recordService;
        _maxConcurrent = settings.MaxConcurrentDownloads;
        _engine = new DownloadEngine(settings.MaxSegmentThreads);
        _infoService = new VideoInfoService();
        _concurrencySemaphore = new SemaphoreSlim(_maxConcurrent, _maxConcurrent);

        // 启动时加载历史下载记录
        LoadHistory();
    }

    /// <summary>
    /// Load completed download records from SQLite into the queue display.
    /// These are read-only history entries — no re-downloading.
    /// </summary>
    private void LoadHistory()
    {
        foreach (var rec in _recordService.GetAllRecords())
        {
            Items.Add(new DownloadItem
            {
                Title = rec.Title,
                Url = rec.Url,
                OutputPath = rec.FilePath,
                Quality = rec.Quality,
                Progress = 100,
                IsCompleted = System.IO.File.Exists(rec.FilePath),
                Status = System.IO.File.Exists(rec.FilePath) ? "已完成" : "文件已删除",
                Speed = string.Empty,
                AddedAt = rec.DownloadedAt,
                CancellationSource = new CancellationTokenSource()
            });
        }
    }

    /// <summary>
    /// Add a new download to the queue and start it (subject to concurrency limit).
    /// Returns false if the same URL is already in the queue (in-memory dedup).
    /// </summary>
    public bool Enqueue(VideoInfo info, string qualityKey)
    {
        // 内存去重：检查同一URL是否已在队列中（排队中/下载中）
        if (Items.Any(i => string.Equals(i.Url, info.Url, StringComparison.OrdinalIgnoreCase)
            && !i.IsCompleted && !i.HasFailed))
        {
            return false; // 已在队列中，跳过
        }

        var safeTitle = MakeSafeFileName(info.Title);
        var ext = ".mp4";
        var subDir = _settings.CreateSubfolders && !string.IsNullOrEmpty(info.Author)
            ? Path.Combine(_settings.OutputDirectory, MakeSafeFileName(info.Author))
            : _settings.OutputDirectory;

        Directory.CreateDirectory(subDir);
        var outputPath = Path.Combine(subDir, safeTitle + ext);
        outputPath = EnsureUniquePath(outputPath);

        var item = new DownloadItem
        {
            Title = info.Title,
            Url = info.Url,
            OutputPath = outputPath,
            Quality = qualityKey,
            Site = info.Site,
            Status = "Queued"
        };

        App.Current.Dispatcher.Invoke(() => Items.Add(item));
        _ = StartDownloadAsync(item, info, qualityKey);
        return true;
    }

    private async Task StartDownloadAsync(DownloadItem item, VideoInfo info, string qualityKey)
    {
        await _concurrencySemaphore.WaitAsync(item.CancellationSource.Token);

        try
        {
            if (item.CancellationSource.Token.IsCancellationRequested)
            {
                item.Status = "Cancelled";
                return;
            }

            item.Status = "Downloading";

            var streamUrl = await _infoService.ResolveStreamUrlAsync(info, qualityKey,
                item.CancellationSource.Token);

            // Pass the original page URL as Referer (required for PornHub CDN auth)
            await _engine.DownloadAsync(
                streamUrl,
                item.OutputPath,
                pageUrl: info.Url,
                onProgress: (pct, speed) =>
                {
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        if (pct >= 0) item.Progress = pct;
                        item.Speed = speed;
                        item.Status = pct >= 100 ? "Completed" : "Downloading";
                    });
                },
                ct: item.CancellationSource.Token);

            item.Progress = 100;
            item.Status = "Completed";
            item.IsCompleted = true;

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
            item.Status = "Cancelled";
            // Clean up partial file
            try { if (File.Exists(item.OutputPath)) File.Delete(item.OutputPath); } catch { }
        }
        catch (Exception ex)
        {
            item.Status = $"Failed: {ex.Message}";
            item.HasFailed = true;
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    public void Cancel(DownloadItem item)
    {
        item.CancellationSource.Cancel();
        item.Status = "Cancelling...";
    }

    public void CancelAll()
    {
        foreach (var item in Items.Where(i => !i.IsCompleted && !i.HasFailed))
            Cancel(item);
    }

    /// <summary>
    /// 清除所有非下载中的任务（Completed/Failed/Cancelled/已删除等），并同步清理数据库记录。
    /// 如果队列为空，清空整个数据库。
    /// </summary>
    public void RemoveCompleted()
    {
        // 只保留"下载中"状态的任务
        var notDownloading = Items.Where(i =>
            !i.Status.Contains("下载") &&
            !i.Status.Contains("Downloading") &&
            !i.Status.Contains("下载中") &&
            !i.Status.Contains("Connecting")).ToList();

        // 从数据库删除被清除项的记录
        foreach (var item in notDownloading)
        {
            _recordService.DeleteRecord(item.Url);
        }

        App.Current.Dispatcher.Invoke(() =>
        {
            foreach (var item in notDownloading) Items.Remove(item);
        });

        // 如果队列为空，清空整个数据库
        if (Items.Count == 0)
        {
            _recordService.ClearAll();
        }
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

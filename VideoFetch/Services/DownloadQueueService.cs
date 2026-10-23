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
///
/// 队列同时负责任务的“生命周期动作”（取消/重试/删除/恢复/彻底删除），界面层只做
/// 展示与转发。所有动作都返回 bool 而不是静默 return —— 用户点了按钮必须能分辨
/// 到底生效了没有。
/// </summary>
public class DownloadQueueService
{
    private readonly AppSettings _settings;
    private readonly DownloadRecordService _recordService;
    private readonly IVideoInfoProvider _infoProvider;
    private readonly Func<IDownloadRunner> _runnerFactory;
    private readonly IUiDispatcher _ui;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly PendingQueueStore _pendingStore;
    private readonly Action<string> _openFolder;
    private int _currentMaxConcurrent;

    public ObservableCollection<DownloadItem> Items { get; } = new();

    public DownloadQueueService(AppSettings settings, DownloadRecordService recordService)
        : this(settings, recordService,
               new VideoInfoService(),
               () => new DownloadEngineRunner(settings.MaxSegmentThreads),
               new WpfUiDispatcher())
    {
    }

    /// <summary>
    /// 测试/替换实现用构造函数：注入信息解析、下载执行与 UI 调度接缝。
    /// </summary>
    public DownloadQueueService(
        AppSettings settings,
        DownloadRecordService recordService,
        IVideoInfoProvider infoProvider,
        Func<IDownloadRunner> runnerFactory,
        IUiDispatcher ui,
        PendingQueueStore? pendingStore = null,
        Action<string>? openFolder = null)
    {
        _settings = settings;
        _recordService = recordService;
        _infoProvider = infoProvider;
        _runnerFactory = runnerFactory;
        _ui = ui;
        _pendingStore = pendingStore ?? new PendingQueueStore();
        _openFolder = openFolder ?? (path => System.Diagnostics.Process.Start(
            "explorer.exe", $"/select,\"{path}\""));
        _currentMaxConcurrent = ClampConcurrency(settings.MaxConcurrentDownloads);
        // 上限必须给足：ApplySettings 会在运行中把并发数调高，用 Release 补额度。
        // 上限若等于初始值，Release 会抛 "Adding the specified count to the
        // semaphore would cause it to exceed its maximum count"（改设置直接报错）。
        _concurrencySemaphore = new SemaphoreSlim(_currentMaxConcurrent, MaxConcurrency);

        // 启动时加载历史下载记录
        LoadHistory();

        // 再把上次没下完的任务捞回来继续下
        RestorePending();
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
        var target = ClampConcurrency(_settings.MaxConcurrentDownloads);
        if (target <= _currentMaxConcurrent) return;   // 调小要等重启才生效

        _concurrencySemaphore.Release(target - _currentMaxConcurrent);
        _currentMaxConcurrent = target;
    }

    /// <summary>并发数的合法区间。上限只是防止手滑输入天文数字。</summary>
    public const int MaxConcurrency = 64;

    private static int ClampConcurrency(int value) => Math.Clamp(value, 1, MaxConcurrency);

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
                IsFileMissing = !fileExists,
                State = fileExists ? DownloadState.Completed : DownloadState.FileMissing,
                Speed = string.Empty,
                AddedAt = rec.DownloadedAt,
                Duration = rec.Duration,
                FileSize = rec.FileSize,
                IsActive = false
            });
        }
    }

    /// <summary>
    /// 把当前未完成的任务写进清单。
    ///
    /// 快照必须在 UI 线程上拍：完成/失败回调可能来自下载线程，此时 UI 线程
    /// 可能正在 Add/Remove，直接枚举 ObservableCollection 会抛
    /// InvalidOperationException，而 Save 内部只记日志 —— 结果是清单静默停在旧内容。
    /// </summary>
    private void SavePending()
    {
        List<DownloadItem>? snapshot = null;
        try
        {
            // IUiDispatcher.Invoke 是 void，用局部变量接结果
            _ui.Invoke(() => snapshot = Items.ToList());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存待续清单失败（取快照时）: {ex.Message}");
            return;
        }

        if (snapshot == null) return;
        _pendingStore.Save(snapshot);
    }

    /// <summary>输出文件是否是"会被追加写入"的 HLS 半成品（.ts/.m4s）。</summary>
    private static bool IsPartialHlsFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return ext.Equals(".ts", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".m4s", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".aac", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把上次退出时没完成的任务放回队列。
    ///
    /// 上次还在排队/下载中的 → 立刻重新开跑（从头下，不做断点续传）；
    /// 上次已经失败/取消的 → 只恢复成原来的样子，等用户点重试，
    /// 否则每次开机都会去撞同一个坏链接。
    /// </summary>
    private void RestorePending()
    {
        var tasks = _pendingStore.Load();
        if (tasks.Count == 0) return;

        var restored = 0;
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Url)) continue;
            if (Items.Any(i => string.Equals(i.Url, task.Url, StringComparison.OrdinalIgnoreCase)))
                continue;   // 已经在列表里（历史记录或本次已入队）

            var item = new DownloadItem
            {
                Url = task.Url,
                Title = task.Title,
                Site = task.Site,
                Quality = task.Quality,
                OutputPath = task.OutputPath,
                Duration = task.Duration,
                AddedAt = task.AddedAt,
                State = task.State
            };

            if (task.WasActive)
            {
                // 只删"确实可能是半截"的文件：HLS 的 .ts 是追加拼接的，残留会让
                // 结果坏掉；而直连下载用 FileMode.Create 覆盖，留着也没关系。
                // 以前无条件删，碰上"文件已下完、下载记录还没写"的窗口
                // （断电/杀进程）就会把用户下好的成品删掉重下。
                if (IsPartialHlsFile(item.OutputPath))
                {
                    try { if (File.Exists(item.OutputPath)) File.Delete(item.OutputPath); } catch { }
                }

                item.Progress = 0;
                item.State = DownloadState.Queued;
                item.IsActive = true;
                Items.Add(item);
                _ = RunQueuedAsync(item, knownInfo: null);
            }
            else
            {
                item.HasFailed = task.State == DownloadState.Failed;
                item.IsCancelled = task.State == DownloadState.Cancelled;
                Items.Add(item);
            }
            restored++;
        }

        if (restored > 0)
            AppLog.Info($"恢复了 {restored} 个未完成的任务");
    }

    // ══════════════════════════════════════════════════════════
    //  ENQUEUE
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// Add a new download to the queue and start it (subject to concurrency limit).
    /// Returns false if the same URL is already active in the queue (in-memory dedup).
    /// </summary>
    /// <param name="durationHint">
    /// 详情页没给时长时用它兜底（搜索结果/分类列表里本来就解析出了时长，
    /// 91pinse 的详情页只能靠这个）。
    /// </param>
    public bool Enqueue(VideoInfo info, string qualityKey, string? durationHint = null)
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
            Duration = string.IsNullOrWhiteSpace(info.Duration) ? durationHint ?? "" : info.Duration,
            State = DownloadState.Queued,
            IsActive = true
        };

        _ui.Invoke(() => Items.Add(item));
        SavePending();                  // 记下来：程序被关掉也能接着下
        _ = RunQueuedAsync(item, info);
        return true;
    }

    /// <summary>
    /// 重新下载一个失败或被取消的任务。
    ///
    /// 重新入队时换一个全新的 CancellationTokenSource —— 旧的一定已经被取消
    /// 或已随失败流程结束，复用会让新任务立刻自我取消。
    /// </summary>
    public bool Retry(DownloadItem item)
    {
        if (item is null) return false;
        if (item.IsActive) return false;               // 已经在跑/在排队
        if (item.IsCompleted) return false;            // 已完成的不叫重试
        if (!item.HasFailed && !item.IsCancelled) return false;
        if (string.IsNullOrWhiteSpace(item.Url)) return false;

        // 同一 URL 已有别的活跃任务时不重复排队
        if (Items.Any(i => !ReferenceEquals(i, item)
                           && string.Equals(i.Url, item.Url, StringComparison.OrdinalIgnoreCase)
                           && i.IsActive))
            return false;

        item.CancellationSource = new CancellationTokenSource();
        item.HasFailed = false;
        item.IsCancelled = false;
        item.IsDeleted = false;
        item.IsFileMissing = false;
        item.StateBeforeTrash = null;
        item.Progress = 0;
        item.Speed = string.Empty;
        item.State = DownloadState.Queued;
        item.IsActive = true;
        SavePending();

        _ = RunQueuedAsync(item, knownInfo: null);
        return true;
    }

    /// <summary>
    /// 核心执行流程：等并发许可 → 取信息 → 解析流地址 → 下载 → 收尾。
    /// </summary>
    private async Task RunQueuedAsync(DownloadItem item, VideoInfo? knownInfo)
    {
        // 记下本次运行使用的取消源。用户取消后立刻点重试时，旧任务的收尾逻辑
        // 还在异步路上，如果不认领自己的“这一轮”，它会把新任务的状态覆盖成
        // Cancelled/Failed —— 表现就是“重试了但还是失败”。
        var cts = item.CancellationSource;
        var token = cts.Token;
        bool IsCurrentRun() => ReferenceEquals(item.CancellationSource, cts);

        // Cancel while still queued → WaitAsync throws before the semaphore is held
        try
        {
            await _concurrencySemaphore.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentRun()) MarkCancelled(item);
            return;
        }

        try
        {
            if (!IsCurrentRun()) return;        // 已被重试接管，本次作废
            if (token.IsCancellationRequested)
            {
                MarkCancelled(item);
                return;
            }

            item.State = DownloadState.Downloading;
            item.IsDownloading = true;

            // 重试路径没有缓存的 VideoInfo，重新抓一次页面
            var info = knownInfo ?? await _infoProvider.FetchAsync(item.Url, token);
            var streamUrl = await _infoProvider.ResolveStreamUrlAsync(info, item.Quality, token);

            var runner = _runnerFactory();

            // Pass the original page URL as Referer (required for PornHub CDN auth)
            var finalPath = await runner.DownloadAsync(
                streamUrl,
                item.OutputPath,
                pageUrl: info.Url,
                onProgress: (pct, speed) =>
                {
                    // Post: never block download threads on the UI thread
                    _ui.Post(() =>
                    {
                        if (pct >= 0) item.Progress = pct;
                        item.Speed = speed;
                        item.State = pct >= 100 ? DownloadState.Completed : DownloadState.Downloading;
                    });
                },
                ct: token);

            // 引擎在某些路径上会把取消当成成功返回（例如 ffmpeg 重封装期间取消），
            // 这里必须复查一次：否则取消的任务会被标成"已完成"，还写进下载记录。
            if (token.IsCancellationRequested)
            {
                if (IsCurrentRun())
                {
                    MarkCancelled(item);
                    DeletePartialFile(item);
                }
                return;
            }

            // HLS remux may have produced a different (mp4) file name
            if (!IsCurrentRun()) return;        // 结果已作废，别去覆写新一轮的状态
            item.OutputPath = finalPath;
            item.Progress = 100;
            item.Speed = string.Empty;      // 结束时不再有速度，别留着上一次的文案
            item.CompletedAt = DateTime.Now;
            item.State = DownloadState.Completed;
            item.IsCompleted = true;
            item.HasFailed = false;
            item.IsCancelled = false;
            item.IsDownloading = false;

            // 下载完成后记录到SQLite（仅在文件保存成功后）
            try
            {
                var fileSize = System.IO.File.Exists(item.OutputPath)
                    ? new System.IO.FileInfo(item.OutputPath).Length : 0L;
                item.FileSize = fileSize;
                var siteName = item.Site.ToString();
                _recordService.InsertRecord(item.Url, item.Title, siteName,
                    item.Quality, item.OutputPath, fileSize, item.Duration);
            }
            catch { /* 记录失败不影响下载流程 */ }

            // 最后才清掉“活跃”：界面以 IsActive=false 作为任务结束的信号，
            // 先置位会让去重记录还没写好的瞬间被当成已完成，用户能立刻重复入队。
            item.IsActive = false;

            // 必须放在 IsActive=false 之后：SavePending 按 IsActive 记 WasActive，
            // 提前写会把刚下完的任务当成"还在下载"，下次启动又下一遍。
            SavePending();

            // 打开文件夹是"锦上添花"，失败绝不能把已完成的任务标成失败 ——
            // 它以前在同一个 try 里，Process.Start 抛异常会被下面的 catch 接住，
            // 于是刚下好的成品被 MarkFailed + DeletePartialFile 删掉。
            if (_settings.AutoOpenFolder)
            {
                try { _openFolder(item.OutputPath); }
                catch (Exception openEx) { AppLog.Warn($"打开文件夹失败（不影响下载结果）: {openEx.Message}"); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (IsCurrentRun())
            {
                MarkCancelled(item);
                DeletePartialFile(item);
            }
        }
        catch (Exception ex)
        {
            // 走到这里包含"token 没被取消却抛 OCE"的情况 —— 典型是 HttpClient 的
            // 60 秒超时（TaskCanceledException 继承自 OperationCanceledException）。
            // 以前它会被上面那个 catch 当成"用户取消"，失败原因整个丢掉，
            // 用户看到的是"已取消"，还以为是误触。
            if (IsCurrentRun())
            {
                MarkFailed(item, ex);
                // Clean up partial file so the dedup check (file exists) isn't fooled
                DeletePartialFile(item);
            }
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    private void MarkCancelled(DownloadItem item)
    {
        item.Speed = string.Empty;
        item.Speed = string.Empty;
        item.Note = string.Empty;   // 不清的话状态列永远停在"正在取消…"
        item.State = DownloadState.Cancelled;
        item.IsCancelled = true;
        item.HasFailed = false;
        item.IsActive = false;
        item.IsDownloading = false;
        SavePending();      // 取消的也留着，重启后还能看到并重试
    }

    private void MarkFailed(DownloadItem item, Exception ex)
    {
        item.Speed = string.Empty;
        item.Note = string.Empty;   // 同上：清掉"正在取消…"，让失败原因能显示出来
        item.State = DownloadState.Failed;
        item.LastError = ex.Message;
        item.HasFailed = true;
        item.IsCancelled = false;
        item.IsActive = false;
        item.IsDownloading = false;
        SavePending();
    }

    private static void DeletePartialFile(DownloadItem item)
    {
        try { if (File.Exists(item.OutputPath)) File.Delete(item.OutputPath); } catch { }
    }

    // ══════════════════════════════════════════════════════════
    //  LIFECYCLE ACTIONS
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 取消一个正在排队/下载的任务。
    ///
    /// 只对活跃任务有意义：已完成、已失败、已取消的任务没有可取消的下载，
    /// 返回 false 让界面知道这次点击不该发生（旧实现直接静默 return，
    /// 失败行上的“取消”按钮点了毫无反应，就是这个原因）。
    /// </summary>
    public bool TryCancel(DownloadItem item)
    {
        if (item is null || !item.IsActive) return false;

        item.CancellationSource.Cancel();

        if (!item.IsDownloading)
        {
            // 还在排队、没有真正开始下载：取消是即刻完成的，直接落到终态。
            // 停在 "Cancelling..." 会留下一个既不能取消也不能重试的空窗。
            MarkCancelled(item);
            return true;
        }

        // A running download exits its loop asynchronously; show the intent first.
        item.Note = LanguageService.GetString("State_Cancelling");
        return true;
    }

    public void CancelAll()
    {
        foreach (var item in Items.Where(i => i.IsActive).ToList())
            TryCancel(item);
    }

    /// <summary>
    /// 一键重试：把所有失败/已取消的任务重新排队，返回真正排上的数量。
    ///
    /// 跳过的：正在跑的、已完成的、丢进回收站的（用户自己移走的就别偷偷拉回来）。
    /// 同一 URL 已有活跃任务时 <see cref="Retry"/> 自己会拒绝，这里不重复判断。
    /// </summary>
    public int RetryAllFailed()
    {
        var candidates = Items
            .Where(i => !i.IsActive
                        && !i.IsCompleted
                        && !i.IsDeleted
                        && !i.IsFileMissing
                        && (i.HasFailed || i.IsCancelled))
            .ToList();

        var started = 0;
        foreach (var item in candidates)
        {
            if (Retry(item)) started++;
        }
        return started;
    }

    /// <summary>
    /// 删除任务（软删除 → 移入回收站，文件与去重记录保留）。
    /// 仅对已结束（完成/失败/取消）的任务有效，正在下载的必须先取消。
    /// </summary>
    public bool DeleteToTrash(DownloadItem item)
    {
        if (item is null || item.IsActive || item.IsDeleted) return false;

        // 记住原状态，恢复时要还原（失败任务恢复后还得显示失败原因）
        item.StateBeforeTrash = item.State;
        item.IsDeleted = true;
        item.Note = string.Empty;
        item.State = DownloadState.Deleted;
        SavePending();      // 进回收站的不该在下次启动时冒出来
        return true;
    }

    /// <summary>
    /// 从回收站恢复任务。
    ///
    /// 只有“已完成”的任务才应该因为文件缺失继续留在回收站；失败/取消的任务
    /// 本来就没有文件，旧实现会在这里把它立刻又设回 IsFileMissing →
    /// 恢复等于没恢复，于是永远出不了回收站。
    /// </summary>
    public bool Restore(DownloadItem item)
    {
        if (item is null || !item.IsDeleted) return false;

        // 已完成但文件已经不在：恢复出来是个坏任务。标记成文件丢失（留在
        // 回收站，只等彻底删除），并如实返回 false，别做无声的空操作。
        if (item.IsCompleted && !File.Exists(item.OutputPath))
        {
            item.IsFileMissing = true;
            return false;
        }

        item.IsDeleted = false;
        item.IsFileMissing = false;

        item.Note = string.Empty;
        item.State = item.StateBeforeTrash ?? DeriveState(item);
        item.StateBeforeTrash = null;
        SavePending();
        return true;
    }

    private static DownloadState DeriveState(DownloadItem item)
        => item.IsCompleted ? DownloadState.Completed
         : item.HasFailed ? DownloadState.Failed
         : item.IsCancelled ? DownloadState.Cancelled
         : DownloadState.Queued;

    /// <summary>彻底删除：移出列表；文件已丢失时一并清掉去重记录。</summary>
    public bool Purge(DownloadItem item)
    {
        if (item is null || item.IsActive) return false;
        PurgeItems(new List<DownloadItem> { item });
        return true;
    }

    /// <summary>清空回收站，返回清掉的条目数。</summary>
    public int EmptyTrash()
    {
        var trash = Items.Where(i => i.IsInTrash).ToList();
        PurgeItems(trash);
        return trash.Count;
    }

    private void PurgeItems(IList<DownloadItem> items)
    {
        if (items.Count == 0) return;

        foreach (var item in items)
        {
            // 文件还在磁盘上 → 保留记录（去重仍有效）；文件已丢失 → 记录没意义，清掉
            if (!File.Exists(item.OutputPath))
                _recordService.DeleteRecord(item.Url);
        }

        _ui.Invoke(() =>
        {
            foreach (var item in items) Items.Remove(item);
        });

        SavePending();
    }

    // ══════════════════════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════════════════════

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Length > 120 ? safe[..120] : safe;
    }

    /// <summary>
    /// 找一个可用文件名：既不能已存在于磁盘，也不能被队列里其它任务占着。
    ///
    /// 只查磁盘不够 —— 文件要等下载开始才创建，同一批里两个同名视频（这些站点
    /// 标题重复率很高）会拿到同一个路径：并行时第二个 FileShare.None 打开失败，
    /// 串行时后一个直接覆盖前一个。
    /// </summary>
    private string EnsureUniquePath(string path)
        => EnsureUniquePath(path, Items.Select(i => i.OutputPath));

    private static string EnsureUniquePath(string path, IEnumerable<string> reservedPaths)
    {
        var taken = new HashSet<string>(
            reservedPaths.Where(p => !string.IsNullOrEmpty(p)),
            StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path) && !taken.Contains(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !taken.Contains(candidate)) return candidate;
        }
        return path;
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoFetch.Models;
using VideoFetch.Services;

namespace VideoFetch.Test;

/// <summary>
/// 离线下载队列测试。
///
/// 队列通过三个接缝（信息解析 / 下载执行 / UI 调度）跑在假实现上，所以能脱网、
/// 确定性地覆盖状态机：入队、去重、并发上限、失败、取消、重试、删除、恢复、
/// 彻底删除，以及行内按钮的可见性矩阵。
///
/// 重点回归两个线上 bug：
///   1. 失败任务上的“取消”按钮点了完全没反应（TryCancel 静默 return）；
///   2. 失败任务没有删除入口，且恢复后会被立刻塞回回收站（死循环）。
/// </summary>
internal static class DownloadQueueTests
{
    private static int _failed;

    public static async Task<bool> RunAsync()
    {
        Console.WriteLine("\n[download-queue]");
        var before = _failed;

        await Safe("enqueue", EnqueueCompletesAsync);
        await Safe("dedup", DuplicateEnqueueRejectedAsync);
        await Safe("same-title", SameTitleGetsDistinctPathsAsync);
        await Safe("concurrency", ConcurrencyLimitAsync);
        await Safe("concurrency-raise", RaiseConcurrencyAsync);
        await Safe("file-size", FileSizeAsync);
        await Safe("pending", PendingAsync);
        await Safe("resume", ResumeAfterRestartAsync);
        await Safe("failure", FailureIsRecordedAsync);
        await Safe("cancel-failed", CancelOnFailedIsRefusedAsync);
        await Safe("delete-failed", DeleteFailedTaskAsync);
        await Safe("restore-failed", RestoreFailedTaskStaysOutOfTrashAsync);
        await Safe("restore-missing", RestoreCompletedWithMissingFileAsync);
        await Safe("retry", RetryFailedTaskAsync);
        await Safe("retry-all", RetryAllFailedAsync);
        await Safe("duration-time", DurationAndTimeAsync);
        await Safe("cancel-running", CancelRunningTaskAsync);
        await Safe("cancel-then-retry", CancelThenRetryImmediatelyAsync);
        await Safe("purge", PurgeTaskAsync);
        await Safe("empty-trash", EmptyTrashAsync);
        SafeSync("buttons", ButtonVisibilityMatrix);

        return _failed == before;
    }

    private static async Task Safe(string name, Func<Task> test)
    {
        try { await test(); }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
    }

    private static void SafeSync(string name, Action test)
    {
        try { test(); }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  1. 正常完成
    // ══════════════════════════════════════════════════════════

    private static async Task EnqueueCompletesAsync()
    {
        using var h = new Harness();

        var item = await h.EnqueueAndWaitAsync(Info("https://site.test/v/1", "正常任务"));

        Check("enqueue: 任务进入队列", h.Queue.Items.Count == 1);
        Check("enqueue: 完成标记", item.IsCompleted);
        Check("enqueue: 进度 100", Math.Abs(item.Progress - 100) < 0.01);
        Check("enqueue: 不再活跃", !item.IsActive);
        Check("enqueue: 状态 Completed", item.State == DownloadState.Completed);
        Check("enqueue: 失败标记未置位", !item.HasFailed);
        Check("enqueue: 文件已落盘", File.Exists(item.OutputPath));
        Check("enqueue: 写入下载记录（去重依据）", h.Records.GetByUrl(item.Url) != null);
        Check("enqueue: 用了页面地址作 Referer", h.Runner.PageUrls.All(u => u == item.Url));
    }

    // ══════════════════════════════════════════════════════════
    //  2. 去重
    // ══════════════════════════════════════════════════════════

    private static async Task DuplicateEnqueueRejectedAsync()
    {
        using var h = new Harness();
        var gate = h.HoldRunner();

        var info = Info("https://site.test/v/2", "重复任务");
        var first = h.Queue.Enqueue(info, QualityKey);
        await WaitUntilAsync(() => h.Queue.Items.Any(i => i.IsDownloading));

        var second = h.Queue.Enqueue(info, QualityKey);

        Check("dedup: 第一次入队成功", first);
        Check("dedup: 活跃期间重复入队被拒绝", !second);
        Check("dedup: 队列里只有一条", h.Queue.Items.Count == 1);

        gate.ReleaseAll();
        await WaitUntilAsync(() => h.Queue.Items.All(i => !i.IsActive));
    }

    // ══════════════════════════════════════════════════════════
    //  2b. 同名视频不能写同一个文件（Critical-1）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 这些站点标题重复率极高（"Amateur wife sucks cock" 一大把）。文件名取自
    /// 标题，如果只判断磁盘上有没有同名文件，同一批里两个同名视频会拿到同一个
    /// 路径：并行时第二个 FileShare.None 打开失败，串行时后一个覆盖前一个。
    /// </summary>
    private static async Task SameTitleGetsDistinctPathsAsync()
    {
        using var h = new Harness(maxConcurrent: 2);
        var gate = h.HoldRunner();

        const string title = "Amateur wife sucks cock";
        h.Queue.Enqueue(Info("https://site.test/v/same-1", title), QualityKey);
        h.Queue.Enqueue(Info("https://site.test/v/same-2", title), QualityKey);
        await WaitUntilAsync(() => h.Queue.Items.Count == 2);

        var paths = h.Queue.Items.Select(i => i.OutputPath).ToList();
        Console.WriteLine($"        PROBE {paths[0]}");
        Console.WriteLine($"        PROBE {paths[1]}");
        Check("same-title: 两个任务的输出路径不同",
            !string.Equals(paths[0], paths[1], StringComparison.OrdinalIgnoreCase));

        gate.ReleaseAll();
        await WaitUntilAsync(() => h.Queue.Items.All(i => !i.IsActive), 15000);

        Check("same-title: 两个任务都下载成功",
            h.Queue.Items.Count(i => i.IsCompleted) == 2);
        Check("same-title: 两个文件都在磁盘上",
            h.Queue.Items.All(i => File.Exists(i.OutputPath)));
    }

    // ══════════════════════════════════════════════════════════
    //  3. 并发上限
    // ══════════════════════════════════════════════════════════

    private static async Task ConcurrencyLimitAsync()
    {
        using var h = new Harness(maxConcurrent: 1);
        var gate = h.HoldRunner();

        h.Queue.Enqueue(Info("https://site.test/v/a", "A"), QualityKey);
        h.Queue.Enqueue(Info("https://site.test/v/b", "B"), QualityKey);

        await WaitUntilAsync(() => h.Queue.Items.Any(i => i.IsDownloading));
        var running = h.Queue.Items.Count(i => i.IsDownloading);
        Console.WriteLine($"  info  同时下载数 = {running}（上限 1）");
        Check("concurrency: 只跑一个", running == 1);
        Check("concurrency: 另一个还在排队",
            h.Queue.Items.Count(i => i.IsActive && !i.IsDownloading) == 1);

        gate.ReleaseAll();
        await WaitUntilAsync(() => h.Queue.Items.All(i => !i.IsActive), 15000);

        // 第二个必须是在第一个释放令牌之后才开始的
        Check("concurrency: 排队的任务最终都完成",
            h.Queue.Items.Count(i => i.IsCompleted) == 2);
        Check("concurrency: 两次下载串行执行", h.Runner.Calls.Count == 2);
    }

    /// <summary>
    /// 运行中调大并发数（设置页改完点保存）。
    ///
    /// 以前 SemaphoreSlim 的上限就是初始值，Release 补额度时会抛
    /// "Adding the specified count to the semaphore would cause it to exceed
    /// its maximum count"，用户看到的是弹窗"出现了一个内部错误"。
    /// </summary>
    private static async Task RaiseConcurrencyAsync()
    {
        using var h = new Harness(maxConcurrent: 1);
        var gate = h.HoldRunner();

        h.Queue.Enqueue(Info("https://site.test/v/c1", "任务1"), QualityKey);
        await WaitUntilAsync(() => h.Queue.Items.Count(i => i.IsDownloading) == 1);

        h.Settings.MaxConcurrentDownloads = 4;
        var threw = false;
        try { h.Queue.ApplySettings(); }
        catch (Exception ex)
        {
            threw = true;
            Console.WriteLine($"  info  ApplySettings 抛了 {ex.GetType().Name}: {ex.Message}");
        }
        Check("concurrency-raise: 调大并发数不抛异常", !threw);

        for (var i = 2; i <= 4; i++)
            h.Queue.Enqueue(Info($"https://site.test/v/c{i}", $"任务{i}"), QualityKey);

        await WaitUntilAsync(() => h.Queue.Items.Count(i => i.IsDownloading) >= 4, 8000);
        Check("concurrency-raise: 调大后真的并行 4 个",
            h.Queue.Items.Count(i => i.IsDownloading) == 4,
            h.Queue.Items.Count(i => i.IsDownloading).ToString());

        // 调小不报错，只是要等重启才生效
        h.Settings.MaxConcurrentDownloads = 1;
        var threw2 = false;
        try { h.Queue.ApplySettings(); }
        catch { threw2 = true; }
        Check("concurrency-raise: 调小也不抛异常", !threw2);

        gate.ReleaseAll();
        await WaitUntilAsync(() => h.Queue.Items.All(i => !i.IsActive), 15000);
    }

    /// <summary>
    /// 未完成任务要落盘：排队/下载中的重启后接着下；失败/取消的只恢复不重跑；
    /// 完成的和进回收站的必须从清单里消失。
    /// </summary>
    private static async Task PendingAsync()
    {
        using var h = new Harness(maxConcurrent: 1);
        var gate = h.HoldRunner();
        var store = new PendingQueueStore();

        h.Queue.Enqueue(Info("https://site.test/v/p1", "下载中"), QualityKey);
        await WaitUntilAsync(() => h.Queue.Items.Count(i => i.IsDownloading) == 1);
        h.Queue.Enqueue(Info("https://site.test/v/p2", "还在排队"), QualityKey);

        var saved = store.Load();
        Check("pending: 未完成任务写进清单", saved.Count == 2, saved.Count.ToString());
        Check("pending: 记下了活跃标记", saved.Count == 2 && saved.All(t => t.WasActive));
        Check("pending: 记下了输出路径与标题",
            saved.All(t => t.OutputPath.Length > 0 && t.Title.Length > 0));

        gate.ReleaseAll();
        await WaitUntilAsync(() => h.Queue.Items.All(i => !i.IsActive), 15000);
        Check("pending: 完成后从清单移除", store.Load().Count == 0, store.Load().Count.ToString());

        // 清单被写坏（断电/手改）时不能让程序起不来
        File.WriteAllText(Path.Combine(h.PendingDir, "queue.json"), "{ 这不是合法 JSON");
        var broken = new PendingQueueStore();
        var brokenResult = -1;
        try { brokenResult = broken.Load().Count; } catch { brokenResult = -2; }
        Check("pending: 清单损坏时返回空列表而不是抛异常", brokenResult == 0, brokenResult.ToString());

        // 失败的任务留在清单里，但标记为非活跃（重启后不会自动去撞同一个坏链接）
        using var h2 = new Harness();
        h2.Runner.Handler = (_, _, _) => throw new InvalidOperationException("network down");
        var failed = await h2.EnqueueAndWaitAsync(Info("https://site.test/v/p3", "会失败"));
        Check("pending: 失败任务仍写进清单", failed.HasFailed && new PendingQueueStore().Load().Count == 1);

        h2.Queue.DeleteToTrash(failed);
        Check("pending: 进回收站后从清单移除", new PendingQueueStore().Load().Count == 0,
            new PendingQueueStore().Load().Count.ToString());
    }

    /// <summary>
    /// 模拟"下到一半被关掉，再打开"：同一份清单、同一个库，新建一个队列服务。
    /// 上次在下载中的任务应该自己接着下完。
    /// </summary>
    private static async Task ResumeAfterRestartAsync()
    {
        // 不用 using：重启后的服务还要用它的目录和数据库
        var h1 = new Harness(maxConcurrent: 2);
        try
        {
            var gate = h1.HoldRunner();
            h1.Queue.Enqueue(Info("https://site.test/v/r1", "被中断的任务A"), QualityKey);
            h1.Queue.Enqueue(Info("https://site.test/v/r2", "被中断的任务B"), QualityKey);
            await WaitUntilAsync(() => h1.Queue.Items.Count(i => i.IsDownloading) >= 1);

            // 造一个残缺文件：重启后必须先删掉，否则拼出来的文件是坏的
            var first = h1.Queue.Items.First();
            Directory.CreateDirectory(Path.GetDirectoryName(first.OutputPath)!);
            await File.WriteAllTextAsync(first.OutputPath, "半截数据");

            // 闸门不放开 —— 这就是"程序被杀时任务还在下载中"的那一刻
            var store = new PendingQueueStore();
            Check("resume: 中断前清单里有两条", store.Load().Count == 2, store.Load().Count.ToString());

            // —— 重启 ——（同一个清单目录、同一个库；新 runner 正常跑完）
            var provider = new FakeProvider();
            var runner = new FakeRunner();
            var queue = new DownloadQueueService(
                h1.Settings, h1.Records, provider, () => runner, new ImmediateDispatcher());

            Check("resume: 重启后任务回来了", queue.Items.Count == 2, queue.Items.Count.ToString());
            Check("resume: 回来就自动继续下",
                queue.Items.All(i => i.IsActive || i.IsCompleted),
                string.Join(",", queue.Items.Select(i => $"{i.Title}={i.State}")));

            await WaitUntilAsync(() => queue.Items.All(i => !i.IsActive), 15000);
            Check("resume: 续下的任务都完成了", queue.Items.All(i => i.IsCompleted),
                string.Join(",", queue.Items.Select(i => $"{i.Title}={i.State}")));
            Check("resume: 残file 被清掉重新下载", runner.Calls.Count == 2, runner.Calls.Count.ToString());
            Check("resume: 重启后清单被清干净", store.Load().Count == 0, store.Load().Count.ToString());
        }
        finally
        {
            h1.Dispose();   // 这时候才删临时目录（h1 的任务还卡在闸门上）
        }
    }

    /// <summary>文件大小：永远用最大单位（1.2G / 12.3M），未知用占位符。</summary>
    private static async Task FileSizeAsync()
    {
        Check("size: 512 字节", FileSizeFormat.Format(512) == "512B", FileSizeFormat.Format(512));
        Check("size: 1K", FileSizeFormat.Format(1024) == "1K", FileSizeFormat.Format(1024));
        Check("size: 1.5K", FileSizeFormat.Format(1536) == "1.5K", FileSizeFormat.Format(1536));
        Check("size: 12.3M",
            FileSizeFormat.Format((long)(12.3 * 1024 * 1024)) == "12.3M",
            FileSizeFormat.Format((long)(12.3 * 1024 * 1024)));
        Check("size: 1.2G",
            FileSizeFormat.Format((long)(1.2 * 1024 * 1024 * 1024)) == "1.2G",
            FileSizeFormat.Format((long)(1.2 * 1024 * 1024 * 1024)));
        // 1048570 按 1024 算会停在 "1024K"，应该进位成 "1M"
        Check("size: 临界值进位成 1M",
            FileSizeFormat.Format(1048570) == "1M", FileSizeFormat.Format(1048570));
        Check("size: 未知显示占位符",
            FileSizeFormat.Format(0) == "—" && FileSizeFormat.Format(-1) == "—");

        // 真实下载完成后要带上大小
        using var h = new Harness();
        var item = await h.EnqueueAndWaitAsync(Info("https://site.test/v/size", "带大小"));
        Check("size: 完成时记录大小", item.FileSize > 0, item.FileSize.ToString());
        Check("size: 显示文案不是占位符", item.FileSizeText != "—", item.FileSizeText);
    }

    // ══════════════════════════════════════════════════════════
    //  4. 失败
    // ══════════════════════════════════════════════════════════

    private static async Task FailureIsRecordedAsync()
    {
        using var h = new Harness();
        h.Runner.Handler = (_, path, _) =>
        {
            File.WriteAllText(path, "half downloaded");     // 模拟残留下载文件
            throw new InvalidOperationException("Response status code 410 (Gone)");
        };

        var item = await h.EnqueueAndWaitAsync(Info("https://site.test/v/fail", "失败任务"));

        Check("failure: 标记失败", item.HasFailed);
        Check("failure: 不再活跃", !item.IsActive);
        Check("failure: 未标记完成", !item.IsCompleted);
        Check("failure: 状态为 Failed 且带原因",
            item.State == DownloadState.Failed && item.LastError.Contains("410"), item.LastError);
        Check("failure: 残留文件被清掉", !File.Exists(item.OutputPath));
        Check("failure: 不写下载记录", h.Records.GetByUrl(item.Url) == null);

        // 失败路径必须释放并发令牌，否则后面的任务永远排不上
        h.Runner.Handler = null;
        var next = await h.EnqueueAndWaitAsync(Info("https://site.test/v/after-fail", "失败后的新任务"));
        Check("failure: 失败后并发名额已释放", next.IsCompleted);
    }

    // ══════════════════════════════════════════════════════════
    //  5. 失败任务上的“取消”——线上 bug 回归
    // ══════════════════════════════════════════════════════════

    private static async Task CancelOnFailedIsRefusedAsync()
    {
        using var h = new Harness();
        var item = await h.FailedItemAsync("https://site.test/v/fail-cancel");

        var stateBefore = item.State;
        var cancelled = h.Queue.TryCancel(item);

        Check("cancel-failed: 失败任务取消返回 false（按钮不该出现）", !cancelled);
        Check("cancel-failed: 状态不变", item.State == stateBefore);
        Check("cancel-failed: 仍保持失败标记", item.HasFailed && !item.IsCancelled);
        Check("cancel-failed: 界面不提供取消按钮", !item.CanCancel);
        Check("cancel-failed: 界面提供重试按钮", item.CanRetry);
        Check("cancel-failed: 界面提供删除按钮", item.CanDelete);
    }

    // ══════════════════════════════════════════════════════════
    //  6. 删除失败任务
    // ══════════════════════════════════════════════════════════

    private static async Task DeleteFailedTaskAsync()
    {
        using var h = new Harness();
        var item = await h.FailedItemAsync("https://site.test/v/delete");
        var originalState = item.State;

        var deleted = h.Queue.DeleteToTrash(item);

        Check("delete-failed: 删除成功", deleted);
        Check("delete-failed: 进入回收站", item.IsDeleted && item.IsInTrash);
        Check("delete-failed: 状态变为已删除", item.State == DownloadState.Deleted);
        Check("delete-failed: 原状态被保存以便恢复", item.StateBeforeTrash == originalState);

        Check("delete-failed: 重复删除被拒绝", !h.Queue.DeleteToTrash(item));
    }

    // ══════════════════════════════════════════════════════════
    //  7. 恢复失败任务——不能又掉回回收站（线上 bug 回归）
    // ══════════════════════════════════════════════════════════

    private static async Task RestoreFailedTaskStaysOutOfTrashAsync()
    {
        using var h = new Harness();
        var item = await h.FailedItemAsync("https://site.test/v/restore");
        var failState = item.State;
        h.Queue.DeleteToTrash(item);

        var restored = h.Queue.Restore(item);

        Check("restore-failed: 恢复成功", restored);
        Check("restore-failed: 不再标记删除", !item.IsDeleted);
        Check("restore-failed: 不会被当成文件丢失", !item.IsFileMissing);
        Check("restore-failed: 真的离开回收站", !item.IsInTrash);
        Check("restore-failed: 还原失败状态", item.State == failState);
        Check("restore-failed: 失败原因还在", item.LastError.Contains("410"), item.LastError);
        Check("restore-failed: 仍可重试", item.CanRetry);
    }

    // ══════════════════════════════════════════════════════════
    //  7b. 已完成但文件丢失：恢复不该假装成功
    // ══════════════════════════════════════════════════════════

    private static async Task RestoreCompletedWithMissingFileAsync()
    {
        using var h = new Harness();

        // (a) 文件还在 → 正常恢复
        var okItem = await h.EnqueueAndWaitAsync(Info("https://site.test/v/restore-ok", "文件在"));
        h.Queue.DeleteToTrash(okItem);
        Check("restore-ok: 文件还在时恢复成功", h.Queue.Restore(okItem));
        Check("restore-ok: 回到已完成状态", okItem.IsCompleted && !okItem.IsInTrash);
        Check("restore-ok: 状态还原", okItem.State == DownloadState.Completed);

        // (b) 文件已被外部删掉 → 不能恢复，只能彻底删除
        var gone = await h.EnqueueAndWaitAsync(Info("https://site.test/v/restore-gone", "文件没了"));
        h.Queue.DeleteToTrash(gone);
        File.Delete(gone.OutputPath);

        var restored = h.Queue.Restore(gone);
        Check("restore-missing: 恢复返回 false（不是无声失败）", !restored);
        Check("restore-missing: 标记为文件丢失", gone.IsFileMissing);
        Check("restore-missing: 仍留在回收站", gone.IsInTrash);
        Check("restore-missing: 界面不再给恢复按钮", !gone.CanRestore);
        Check("restore-missing: 可以彻底删除", h.Queue.Purge(gone) && !h.Queue.Items.Contains(gone));
    }

    // ══════════════════════════════════════════════════════════
    //  8. 重试
    // ══════════════════════════════════════════════════════════

    private static async Task RetryFailedTaskAsync()
    {
        using var h = new Harness();

        var failOnce = true;
        h.Runner.Handler = (_, path, _) =>
        {
            if (failOnce) { failOnce = false; throw new InvalidOperationException("network down"); }
            File.WriteAllText(path, "ok");
            return Task.FromResult(path);
        };

        var item = await h.EnqueueAndWaitAsync(Info("https://site.test/v/retry", "重试任务"));
        Check("retry: 第一次失败", item.HasFailed);
        var fetchCountBefore = h.Provider.FetchCount;

        // 假下载是同步跑完的，所以不能断言“调用 Retry 之后此刻仍在活跃”，
        // 改为记录状态迁移，确认它确实重新走了一遍排队 → 下载 → 完成。
        var statusLog = new List<string>();
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadItem.State)) statusLog.Add(item.State.ToString());
        };

        var oldCts = item.CancellationSource;
        var retried = h.Queue.Retry(item);

        Check("retry: 允许重试", retried);
        Check("retry: 清掉失败标记", !item.HasFailed && !item.IsCancelled);
        Check("retry: 重新经历排队与下载阶段",
            statusLog.Contains("Queued") && statusLog.Contains("Downloading"));
        Check("retry: 换了新的取消令牌",
            !ReferenceEquals(oldCts, item.CancellationSource) && !item.CancellationSource.IsCancellationRequested);
        Check("retry: 重新抓取页面信息", h.Provider.FetchCount == fetchCountBefore + 1);

        await WaitUntilAsync(() => !item.IsActive);
        Check("retry: 重试后下载成功", item.IsCompleted && item.State == DownloadState.Completed);
        Check("retry: 重试后写入下载记录", h.Records.GetByUrl(item.Url) != null);

        Check("retry: 已完成的任务不能再重试", !h.Queue.Retry(item));
    }

    // ══════════════════════════════════════════════════════════
    //  8b. 一键重试
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 一键重试要满足三件事：把全部失败/已取消的重新排队、跳过已完成和回收站里的、
    /// 再点一次不会重复排队。
    /// </summary>
    private static async Task RetryAllFailedAsync()
    {
        using var h = new Harness(maxConcurrent: 3);

        // 前 3 次下载必失败，之后成功：快速造出多条失败任务
        var budget = 3;
        h.Runner.Handler = (_, path, _) =>
        {
            if (Interlocked.Decrement(ref budget) >= 0)
                throw new InvalidOperationException("network down");

            File.WriteAllText(path, "ok");
            return Task.FromResult(path);
        };

        var failed = new List<DownloadItem>();
        for (var i = 1; i <= 3; i++)
            failed.Add(await h.EnqueueAndWaitAsync(Info($"https://site.test/v/ra{i}", $"批量失败{i}")));

        Check("retry-all: 三条都失败", failed.All(i => i.HasFailed),
            string.Join(",", failed.Select(i => i.HasFailed)));
        Check("retry-all: 失败任务不进回收站", failed.All(i => !i.IsInTrash));

        // 一条成功的（不应被重试）
        var done = await h.EnqueueAndWaitAsync(Info("https://site.test/v/ra-ok", "成功的"));
        Check("retry-all: 第四条成功", done.IsCompleted);

        // 一条被用户丢进回收站的失败任务（也不应被拉回来）
        var trashed = await h.EnqueueAndWaitAsync(
            Info("https://site.test/v/ra-trash", "回收站里的"));
        // 上面这条在 budget 用完后会成功，先删掉再手工造一条失败的最省事
        trashed.IsCompleted = false;
        trashed.HasFailed = true;
        if (!h.Queue.DeleteToTrash(trashed)) Check("retry-all: 能移入回收站", false);

        var downloadsBefore = h.Runner.Calls.Count;
        var started = h.Queue.RetryAllFailed();

        Check("retry-all: 只拉起失败的 3 条（跳过已完成和回收站）", started == 3, started.ToString());
        Check("retry-all: 立即重新排队并开始下载",
            failed.All(i => i.IsActive || i.IsCompleted));

        await WaitUntilAsync(() => failed.All(i => !i.IsActive), 10000);

        Check("retry-all: 重试后都完成", failed.All(i => i.IsCompleted),
            string.Join(",", failed.Select(i => i.State)));
        Check("retry-all: 每条都重新下载了一次", h.Runner.Calls.Count == downloadsBefore + 3,
            $"{h.Runner.Calls.Count - downloadsBefore}");
        Check("retry-all: 回收站里的没被重试", trashed.IsDeleted && !trashed.IsCompleted);
        Check("retry-all: 已完成的不受影响", done.IsCompleted);

        // 已经全部成功/回收 → 再点一次什么也不做
        Check("retry-all: 没有可重试的任务时返回 0", h.Queue.RetryAllFailed() == 0);
    }

    // ══════════════════════════════════════════════════════════
    //  11. 时长与下载时间（队列页新增的两列）
    // ══════════════════════════════════════════════════════════

    private static async Task DurationAndTimeAsync()
    {
        // ① 详情页给了时长
        using (var h = new Harness())
        {
            h.Queue.Enqueue(Info("https://site.test/v/d1", "带时长", "0:02:14"), QualityKey);
            await WaitUntilAsync(() => h.Queue.Items.Count == 1);
            var item = h.Queue.Items[0];

            Check("duration: 用详情页的时长", item.Duration == "0:02:14", item.Duration);
            Check("duration: 秒数", item.DurationSeconds == 134, item.DurationSeconds.ToString());
            Check("duration: 显示文案", item.DurationText == "0:02:14", item.DurationText);

            await WaitUntilAsync(() => !item.IsActive);
            Check("time: 完成时记下时刻", item.CompletedAt.HasValue);
            Check("duration: 时长写进下载记录",
                h.Records.GetByUrl(item.Url)?.Duration == "0:02:14",
                h.Records.GetByUrl(item.Url)?.Duration);
        }

        // ② 详情页没时长 → 用列表里那一条的（91pinse 详情页没有时长节点）
        using (var h = new Harness())
        {
            h.Queue.Enqueue(Info("https://site.test/v/d2", "无时长"), QualityKey, "13分钟");
            await WaitUntilAsync(() => h.Queue.Items.Count == 1);
            var item = h.Queue.Items[0];

            Check("duration: 详情页缺时长时用列表兜底", item.Duration == "13分钟", item.Duration);
            Check("duration: 兜底值也能算秒数", item.DurationSeconds == 780, item.DurationSeconds.ToString());
        }

        // ②b 详情页有时长时优先用详情页（忽略兜底）
        using (var h = new Harness())
        {
            h.Queue.Enqueue(Info("https://site.test/v/d2b", "两者都有", "0:01:00"), QualityKey, "13分钟");
            await WaitUntilAsync(() => h.Queue.Items.Count == 1);
            Check("duration: 详情页优先", h.Queue.Items[0].Duration == "0:01:00", h.Queue.Items[0].Duration);
        }

        // ③ 都没有 → 占位符，不留白
        using (var h = new Harness())
        {
            h.Queue.Enqueue(Info("https://site.test/v/d3", "没有时长"), QualityKey);
            await WaitUntilAsync(() => h.Queue.Items.Count == 1);
            var item = h.Queue.Items[0];

            Check("duration: 没有时长显示占位符", item.DurationText == "—", item.DurationText);
            Check("duration: 没有时长秒数为 0", item.DurationSeconds == 0, item.DurationSeconds.ToString());
        }

        // ④ 下载时间：完成前用入队时间，完成后用完成时间，格式 年-月-日 时:分
        var fresh = new DownloadItem { AddedAt = new DateTime(2026, 10, 23, 9, 7, 0) };
        Check("time: 未完成时用入队时间",
            fresh.DownloadTimeText == "2026-10-23 09:07", fresh.DownloadTimeText);

        fresh.CompletedAt = new DateTime(2026, 10, 23, 21, 5, 0);
        Check("time: 完成后用完成时间",
            fresh.DownloadTimeText == "2026-10-23 21:05", fresh.DownloadTimeText);
        Check("time: 排序键跟着完成时间走",
            fresh.DownloadTime == new DateTime(2026, 10, 23, 21, 5, 0), fresh.DownloadTime.ToString("HH:mm"));

        // ⑤ 结束后不能留着"速度"文案（曾经把引擎的 "Done" 原样显示在状态下面）
        using (var h = new Harness())
        {
            h.Queue.Enqueue(Info("https://site.test/v/speed", "完成后清速度"), QualityKey);
            await WaitUntilAsync(() => h.Queue.Items.Count == 1);
            var item = h.Queue.Items[0];
            await WaitUntilAsync(() => !item.IsActive);
            Check("speed: 完成后速度文案清空", item.Speed.Length == 0, $"'{item.Speed}'");
        }

        // ⑥ 长度文案改动要通知界面（列是绑定的）
        var notified = new List<string>();
        fresh.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? "");
        fresh.Duration = "10 min";
        Check("duration: 改时长会通知界面",
            notified.Contains(nameof(DownloadItem.Duration)) && notified.Contains(nameof(DownloadItem.DurationText)));
    }

    // ══════════════════════════════════════════════════════════
    //  9. 下载中取消
    // ══════════════════════════════════════════════════════════

    private static async Task CancelRunningTaskAsync()
    {
        using var h = new Harness();
        var gate = h.HoldRunner();

        h.Queue.Enqueue(Info("https://site.test/v/cancel", "取消任务"), QualityKey);
        var item = h.Queue.Items[0];
        await WaitUntilAsync(() => item.IsDownloading);

        var cancelled = h.Queue.TryCancel(item);
        Check("cancel-running: 取消被接受", cancelled);
        Check("cancel-running: 先显示正在取消或已取消",
            item.State == DownloadState.Cancelled || item.Note.Length > 0);

        gate.ReleaseAll();
        await WaitUntilAsync(() => !item.IsActive && item.State == DownloadState.Cancelled, 10000);

        Check("cancel-running: 最终状态 Cancelled", item.State == DownloadState.Cancelled);
        Check("cancel-running: 标记为已取消", item.IsCancelled);
        Check("cancel-running: 不算失败", !item.HasFailed);
        Check("cancel-running: 残留文件被清掉", !File.Exists(item.OutputPath));
        Check("cancel-running: 取消后可以重试", item.CanRetry);
        Check("cancel-running: 重复取消返回 false", !h.Queue.TryCancel(item));
    }

    // ══════════════════════════════════════════════════════════
    //  9b. 取消后立刻重试：旧任务的收尾不能覆盖新任务状态
    // ══════════════════════════════════════════════════════════

    private static async Task CancelThenRetryImmediatelyAsync()
    {
        using var h = new Harness(maxConcurrent: 1);
        var gate = h.HoldRunner();

        // A 占住唯一的并发名额，B 只能排队
        h.Queue.Enqueue(Info("https://site.test/v/busy", "占位任务"), QualityKey);
        h.Queue.Enqueue(Info("https://site.test/v/queued", "排队任务"), QualityKey);
        var queued = h.Queue.Items.First(i => i.Url == "https://site.test/v/queued");
        await WaitUntilAsync(() => h.Queue.Items.Any(i => i.IsDownloading) && queued.IsActive);

        // 取消 B 后立刻重试：旧任务还在异步路上等着抛 OperationCanceledException
        h.Queue.TryCancel(queued);
        var retried = h.Queue.Retry(queued);
        Check("cancel-then-retry: 排队中的任务取消后可以立刻重试", retried);

        gate.ReleaseAll();
        await WaitUntilAsync(() => h.Queue.Items.All(i => !i.IsActive), 15000);

        Check("cancel-then-retry: 重试不被旧任务的收尾覆盖",
            queued.IsCompleted && !queued.IsCancelled && !queued.HasFailed);
        Check("cancel-then-retry: 状态最终为 Completed", queued.State == DownloadState.Completed);
    }

    // ══════════════════════════════════════════════════════════
    //  10. 彻底删除
    // ══════════════════════════════════════════════════════════
    private static async Task PurgeTaskAsync()
    {
        using var h = new Harness();

        // (a) 文件已经丢失的失败任务 → 一起清掉去重记录
        var failed = await h.FailedItemAsync("https://site.test/v/purge-fail");
        h.Records.InsertRecord(failed.Url, failed.Title, "XVideos", QualityKey, @"C:\nope\missing.mp4");
        h.Queue.DeleteToTrash(failed);

        Check("purge: 彻底删除成功", h.Queue.Purge(failed));
        Check("purge: 从队列移除", !h.Queue.Items.Contains(failed));
        Check("purge: 清掉无意义的下载记录", h.Records.GetByUrl(failed.Url) == null);

        // (b) 文件还在的完成任务 → 保留记录（去重仍然有效）
        var done = await h.EnqueueAndWaitAsync(Info("https://site.test/v/purge-done", "保留记录"));
        h.Queue.DeleteToTrash(done);
        h.Queue.Purge(done);

        Check("purge: 文件还在时保留下载记录", h.Records.GetByUrl(done.Url) != null);

        // (c) 活跃任务不允许删除
        var gate = h.HoldRunner();
        h.Queue.Enqueue(Info("https://site.test/v/purge-active", "活跃"), QualityKey);
        var active = h.Queue.Items.First(i => i.Url == "https://site.test/v/purge-active");
        Check("purge: 活跃任务不能直接删除", !h.Queue.DeleteToTrash(active) && !h.Queue.Purge(active));
        gate.ReleaseAll();
        await WaitUntilAsync(() => !active.IsActive);
    }

    // ══════════════════════════════════════════════════════════
    //  11. 清空回收站
    // ══════════════════════════════════════════════════════════

    private static async Task EmptyTrashAsync()
    {
        using var h = new Harness();

        var a = await h.FailedItemAsync("https://site.test/v/trash-a");
        var b = await h.EnqueueAndWaitAsync(Info("https://site.test/v/trash-b", "已完成"));
        h.Queue.DeleteToTrash(a);
        h.Queue.DeleteToTrash(b);

        var emptied = h.Queue.EmptyTrash();

        Check("empty-trash: 清掉两条", emptied == 2);
        Check("empty-trash: 队列已空", h.Queue.Items.Count == 0);
        Check("empty-trash: 空回收站再清返回 0", h.Queue.EmptyTrash() == 0);
    }

    // ══════════════════════════════════════════════════════════
    //  12. 行内按钮可见性矩阵（界面的唯一判定依据）
    // ══════════════════════════════════════════════════════════

    private static void ButtonVisibilityMatrix()
    {
        var active = new DownloadItem { IsActive = true, State = DownloadState.Downloading };
        var failed = new DownloadItem { IsActive = false, HasFailed = true, State = DownloadState.Failed, LastError = "x" };
        var cancelled = new DownloadItem { IsActive = false, IsCancelled = true, State = DownloadState.Cancelled };
        var done = new DownloadItem { IsActive = false, IsCompleted = true, State = DownloadState.Completed };
        var trashed = new DownloadItem { IsActive = false, HasFailed = true, IsDeleted = true, State = DownloadState.Deleted };
        var trashedDone = new DownloadItem { IsActive = false, IsCompleted = true, IsDeleted = true, State = DownloadState.Deleted };
        var trashedMissing = new DownloadItem
        {
            IsActive = false, IsCompleted = true, IsDeleted = true,
            IsFileMissing = true, State = DownloadState.Deleted
        };

        AssertButtons("下载中", active, cancel: true, retry: false, delete: false, deleteFile: false);
        AssertButtons("失败", failed, cancel: false, retry: true, delete: true, deleteFile: false);
        AssertButtons("已取消", cancelled, cancel: false, retry: true, delete: true, deleteFile: false);
        AssertButtons("已完成", done, cancel: false, retry: false, delete: true, deleteFile: true);
        AssertButtons("回收站（失败）", trashed, cancel: false, retry: true, delete: false, deleteFile: false, restore: true);
        AssertButtons("回收站（已完成）", trashedDone, cancel: false, retry: false, delete: false, deleteFile: false, restore: true);
        // 文件已经没了 → 恢复出来是个坏任务，只给“彻底删除”
        AssertButtons("回收站（文件丢失）", trashedMissing, cancel: false, retry: false, delete: false, deleteFile: false);

        // 状态变化必须通知界面刷新按钮（否则按钮不会跟着状态变）
        var notified = new List<string>();
        var probe = new DownloadItem();
        probe.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? "");
        probe.HasFailed = true;
        Check("buttons: 状态变化通知了可见性属性",
            notified.Contains(nameof(DownloadItem.CanRetry)) &&
            notified.Contains(nameof(DownloadItem.CanDelete)));
    }

    private static void AssertButtons(string state, DownloadItem item,
        bool cancel, bool retry, bool delete, bool deleteFile, bool restore = false)
    {
        var ok = item.CanCancel == cancel
              && item.CanRetry == retry
              && item.CanDelete == delete
              && item.CanDeleteFile == deleteFile
              && item.CanRestore == restore;
        if (!ok)
            Console.WriteLine($"        {state}: cancel={item.CanCancel} retry={item.CanRetry} " +
                              $"delete={item.CanDelete} deleteFile={item.CanDeleteFile} restore={item.CanRestore}");
        Check($"buttons: {state} → 取消={cancel} 重试={retry} 删除={delete} 删文件={deleteFile}", ok);
    }

    // ══════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════

    private const string QualityKey = "hls_720";

    private static VideoInfo Info(string url, string title, string duration = "") => new()
    {
        Url = url,
        Title = title,
        Site = SiteType.XVideos,
        Duration = duration,
        Qualities = { new QualityOption("720p", QualityKey) },
        StreamUrls = { [QualityKey] = "https://cdn.test/v.m3u8" }
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("等待状态超时");
            await Task.Delay(10);
        }
    }

    private static void Check(string name, bool condition, string? actual = null)
    {
        Console.WriteLine(condition
            ? $"  PASS  {name}"
            : $"  FAIL  {name}" + (actual is null ? "" : $"  (实际: {actual})"));
        if (!condition) _failed++;
    }

    // ── 假实现 ────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public readonly string Root;
        public readonly AppSettings Settings;
        public readonly DownloadRecordService Records;
        public readonly FakeProvider Provider = new();
        public readonly FakeRunner Runner = new();
        public readonly DownloadQueueService Queue;
        public readonly string PendingDir;

        public Harness(int maxConcurrent = 2)
        {
            Root = Path.Combine(Path.GetTempPath(), "vf-dlq-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
            Settings = new AppSettings
            {
                OutputDirectory = Root,
                MaxConcurrentDownloads = maxConcurrent,
                MaxSegmentThreads = 2,
                CreateSubfolders = false,
                AutoOpenFolder = false
            };
            Records = new DownloadRecordService(Path.Combine(Root, "test.db"));

            // 待续清单也放临时目录；静态目录是全局的，但用例是串行跑的，
            // 每个 Harness 构造时重置一次就够（"重启"用例要的就是同一目录）
            PendingDir = Path.Combine(Root, "pending");
            PendingQueueStore.Directory = PendingDir;
            Queue = new DownloadQueueService(Settings, Records, Provider, () => Runner, new ImmediateDispatcher());
        }

        /// <summary>入队并等到任务结束。</summary>
        public async Task<DownloadItem> EnqueueAndWaitAsync(VideoInfo info)
        {
            Queue.Enqueue(info, QualityKey);
            var item = Queue.Items.First(i => i.Url == info.Url);
            await WaitUntilAsync(() => !item.IsActive);
            return item;
        }

        /// <summary>跑一个必定失败的任务并返回它。</summary>
        public async Task<DownloadItem> FailedItemAsync(string url)
        {
            var previous = Runner.Handler;
            Runner.Handler = (_, _, _) => throw new InvalidOperationException("410 Gone");
            try
            {
                return await EnqueueAndWaitAsync(Info(url, "失败"));
            }
            finally
            {
                Runner.Handler = previous;
            }
        }

        /// <summary>让所有下载卡住，直到手动放行 —— 用来看“下载中”的状态。</summary>
        public Gate HoldRunner()
        {
            var gate = new Gate();
            Runner.Handler = async (_, path, ct) =>
            {
                await gate.WaitAsync(ct);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "ok", ct);
                return path;
            };
            return gate;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* SQLite 连接池可能还占着文件 */ }
        }
    }

    private sealed class Gate
    {
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> _waiters = new();
        private volatile bool _open;

        public Task WaitAsync(CancellationToken ct)
        {
            // 开门之后新来的下载直接放行：并发测试里后一个任务是在前一个
            // 释放令牌之后才开始跑的，如果只放行“当前等待者”，它会永远卡住。
            if (_open) return Task.CompletedTask;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_open) return Task.CompletedTask;
            _waiters.Enqueue(tcs);
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        public void ReleaseAll()
        {
            _open = true;
            while (_waiters.TryDequeue(out var tcs)) tcs.TrySetResult(true);
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();
        public void Post(Action action) => action();
    }

    private sealed class FakeProvider : IVideoInfoProvider
    {
        public int FetchCount;
        public int ResolveCount;

        public Task<VideoInfo> FetchAsync(string url, CancellationToken ct = default)
        {
            FetchCount++;
            return Task.FromResult(Info(url, "fake"));
        }

        public Task<string> ResolveStreamUrlAsync(VideoInfo info, string qualityKey, CancellationToken ct = default)
        {
            ResolveCount++;
            return Task.FromResult("https://cdn.test/resolved.m3u8");
        }
    }

    private sealed class FakeRunner : IDownloadRunner
    {
        public readonly List<string> Calls = new();
        public readonly List<string> PageUrls = new();
        public Func<string, string, CancellationToken, Task<string>>? Handler;

        public async Task<string> DownloadAsync(
            string streamUrl, string outputPath, string pageUrl = "",
            Action<double, string>? onProgress = null, CancellationToken ct = default)
        {
            Calls.Add(streamUrl);
            PageUrls.Add(pageUrl);

            if (Handler != null) return await Handler(streamUrl, outputPath, ct);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, "ok", ct);
            onProgress?.Invoke(50, "1.0 MB/s");
            onProgress?.Invoke(100, string.Empty);
            return outputPath;
        }
    }
}

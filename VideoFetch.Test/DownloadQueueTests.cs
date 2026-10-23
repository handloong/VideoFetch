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
        await Safe("failure", FailureIsRecordedAsync);
        await Safe("cancel-failed", CancelOnFailedIsRefusedAsync);
        await Safe("delete-failed", DeleteFailedTaskAsync);
        await Safe("restore-failed", RestoreFailedTaskStaysOutOfTrashAsync);
        await Safe("restore-missing", RestoreCompletedWithMissingFileAsync);
        await Safe("retry", RetryFailedTaskAsync);
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

    private static VideoInfo Info(string url, string title) => new()
    {
        Url = url,
        Title = title,
        Site = SiteType.XVideos,
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

using VideoFetch.Models;

namespace VideoFetch.Services;

/// <summary>
/// UI 调度接缝。
///
/// 下载线程会修改绑定到界面的状态（进度/状态/集合），这些修改必须回到 UI 线程。
/// 把调度抽成接口后，测试可以注入同步实现，在没有 WPF Application 的情况下
/// 把整条下载队列跑起来。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>在 UI 线程同步执行并等待完成。</summary>
    void Invoke(Action action);

    /// <summary>把操作排到 UI 线程，不等待（高频进度刷新用）。</summary>
    void Post(Action action);
}

/// <summary>默认实现：走 <see cref="System.Windows.Application"/> 的 Dispatcher。</summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public void Post(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) action();
        else dispatcher.BeginInvoke(action);
    }
}

/// <summary>
/// 站点信息接缝：队列只依赖这两个能力，测试里可以完全脱网。
/// <see cref="VideoInfoService"/> 是生产实现。
/// </summary>
public interface IVideoInfoProvider
{
    /// <summary>抓取视频页面信息（重试时用 URL 重新取一遍）。</summary>
    Task<VideoInfo> FetchAsync(string url, CancellationToken ct = default);

    /// <summary>把画质 key 解析成真正可下载的流地址。</summary>
    Task<string> ResolveStreamUrlAsync(VideoInfo info, string qualityKey, CancellationToken ct = default);
}

/// <summary>
/// 下载执行接缝。生产实现包一层 <see cref="DownloadEngine"/>。
/// </summary>
public interface IDownloadRunner
{
    /// <summary>下载到 outputPath，返回最终路径（HLS 重封装后可能是 .mp4）。</summary>
    Task<string> DownloadAsync(
        string streamUrl,
        string outputPath,
        string pageUrl = "",
        Action<double, string>? onProgress = null,
        CancellationToken ct = default);
}

/// <summary>生产实现：每次下载新建一个引擎，以便取到最新的 MaxSegmentThreads。</summary>
public sealed class DownloadEngineRunner : IDownloadRunner
{
    private readonly DownloadEngine _engine;

    public DownloadEngineRunner(int maxSegmentThreads) => _engine = new DownloadEngine(maxSegmentThreads);

    public Task<string> DownloadAsync(
        string streamUrl,
        string outputPath,
        string pageUrl = "",
        Action<double, string>? onProgress = null,
        CancellationToken ct = default)
        => _engine.DownloadAsync(streamUrl, outputPath, pageUrl, onProgress, ct);
}

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VideoFetch.Services;

namespace VideoFetch.Test;

/// <summary>
/// 下载链路的离线回归测试（不需要网络）。
///
/// 这几项以前都没法测：HttpClientFactory 是静态的，DownloadEngine 拿不到注入点。
/// 现在把"读空闲超时""内容类型校验""HLS 分段解析"抽成纯函数，就能直接构造输入。
/// </summary>
internal static class CleanupRegressionTests
{
    private static int _failed;

    public static bool Run()
    {
        Console.WriteLine("\n[download-hardening]");
        var before = _failed;

        Safe("idle-timeout", IdleTimeoutAsync);
        Safe("idle-normal", NormalReadAsync);
        Safe("idle-cancel", CancellationIsNotTimeoutAsync);
        Safe("content-type", ContentTypeGuard);
        Safe("byterange", ByteRangeParsing);
        Safe("plain-segments", PlainSegmentsUnchanged);

        return _failed == before;
    }

    /// <summary>CDN 半死：ReadAsync 永远不返回 → 必须超时抛错，而不是永久挂住。</summary>
    private static async Task IdleTimeoutAsync()
    {
        await using var stream = new HangingStream();
        var buffer = new byte[16];

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var threw = false;
        try
        {
            await DownloadEngine.ReadWithIdleTimeoutAsync(stream, buffer, CancellationToken.None,
                TimeSpan.FromMilliseconds(120));
        }
        catch (TimeoutException)
        {
            threw = true;
        }

        Check("idle: 读挂住会抛 TimeoutException", threw);
        Check("idle: 不会拖太久（<3s）", sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds}ms");
    }

    private static async Task NormalReadAsync()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        var buffer = new byte[16];

        var read = await DownloadEngine.ReadWithIdleTimeoutAsync(
            stream, buffer, CancellationToken.None, TimeSpan.FromSeconds(2));

        Check("idle: 正常数据照常返回", read == 5, read.ToString());
    }

    /// <summary>用户取消要抛 OperationCanceledException，不能伪装成超时（否则会变成"失败"）。</summary>
    private static async Task CancellationIsNotTimeoutAsync()
    {
        await using var stream = new HangingStream();
        var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        var gotTimeout = false;
        var gotCancel = false;
        try
        {
            await DownloadEngine.ReadWithIdleTimeoutAsync(stream, new byte[8], cts.Token,
                TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException) { gotTimeout = true; }
        catch (OperationCanceledException) { gotCancel = true; }

        Check("idle: 取消不伪装成超时", gotCancel && !gotTimeout, $"timeout={gotTimeout} cancel={gotCancel}");
    }

    /// <summary>200 + HTML（登录墙/错误页）必须当失败，不能存成几 KB 的"视频"。</summary>
    private static void ContentTypeGuard()
    {
        using var html = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("<html>expired</html>", Encoding.UTF8, "text/html")
        };
        var threw = false;
        try { DownloadEngine.EnsureLooksLikeMedia(html); }
        catch (InvalidDataException) { threw = true; }
        Check("content: 200 + text/html 被拒绝", threw);

        using var json = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"error\":\"denied\"}", Encoding.UTF8, "application/json")
        };
        var threwJson = false;
        try { DownloadEngine.EnsureLooksLikeMedia(json); }
        catch (InvalidDataException) { threwJson = true; }
        Check("content: 200 + json 被拒绝", threwJson);

        using var video = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x47, 0x40, 0x11, 0x10 })
        };
        video.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        var ok = true;
        try { DownloadEngine.EnsureLooksLikeMedia(video); }
        catch { ok = false; }
        Check("content: video/mp4 放行", ok);

        // 没有 Content-Type 的 CDN 也要放行（不能因为没类型就断定失败）
        using var noType = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        };
        noType.Content.Headers.ContentType = null;
        var ok2 = true;
        try { DownloadEngine.EnsureLooksLikeMedia(noType); }
        catch { ok2 = false; }
        Check("content: 没有 Content-Type 时放行", ok2);
    }

    /// <summary>#EXT-X-BYTERANGE：同一个 URI 的多段要带 offset/length，否则会重复下整文件。</summary>
    private static void ByteRangeParsing()
    {
        const string playlist = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-MAP:URI="init.mp4"
            #EXTINF:4.0,
            #EXT-X-BYTERANGE:1000@0
            main.mp4
            #EXTINF:4.0,
            #EXT-X-BYTERANGE:2000@1000
            main.mp4
            #EXTINF:4.0,
            #EXT-X-BYTERANGE:500
            main.mp4
            """;

        var segments = DownloadEngine.ParseSegmentInfos(playlist, new Uri("https://cdn.test/path/index.m3u8"));

        Check("byterange: 解析出 3 段", segments.Count == 3, segments.Count.ToString());
        if (segments.Count != 3) return;

        Check("byterange: 第一段 offset/length",
            segments[0].Offset == 0 && segments[0].Length == 1000,
            $"{segments[0].Offset}/{segments[0].Length}");
        Check("byterange: 第二段 offset/length",
            segments[1].Offset == 1000 && segments[1].Length == 2000,
            $"{segments[1].Offset}/{segments[1].Length}");
        Check("byterange: 省略 @offset 时接上一段末尾",
            segments[2].Offset == 3000 && segments[2].Length == 500,
            $"{segments[2].Offset}/{segments[2].Length}");
        Check("byterange: URL 按 base 解析",
            segments[0].Url == "https://cdn.test/path/main.mp4", segments[0].Url);
    }

    private static void PlainSegmentsUnchanged()
    {
        const string playlist = """
            #EXTM3U
            #EXTINF:4.0,
            seg1.ts
            #EXTINF:4.0,
            https://cdn.test/seg2.ts
            """;

        var infos = DownloadEngine.ParseSegmentInfos(playlist, new Uri("https://cdn.test/path/index.m3u8"));
        Check("segments: 普通列表没有范围（0/0）",
            infos.Count == 2 && infos.All(s => s.Offset == 0 && s.Length == 0),
            infos.Count.ToString());
        Check("segments: 旧的 ParseSegments 行为不变",
            DownloadEngine.ParseSegments(playlist, new Uri("https://cdn.test/path/index.m3u8")).Count == 2);
    }

    /// <summary>永远不返回数据的流，用来模拟"连接活着但不发数据"的 CDN。</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    private static void Safe(string name, Action test)
    {
        try { test(); }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}: 抛异常 {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
    }

    private static void Safe(string name, Func<Task> test)
    {
        try { test().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}: 抛异常 {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
    }

    private static void Check(string name, bool condition, string? actual = null)
    {
        Console.WriteLine(condition
            ? $"  PASS  {name}"
            : $"  FAIL  {name}" + (actual is null ? "" : $"  (实际: {actual})"));
        if (!condition) _failed++;
    }
}

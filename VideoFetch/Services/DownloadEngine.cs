using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using VideoFetch.Models;

namespace VideoFetch.Services;

/// <summary>
/// Download engine supporting:
/// - HLS/M3U8 segmented downloads (parallel segment fetching)
/// - Direct MP4/file downloads
/// Reports progress through Action callbacks.
///
/// PornHub HLS URLs require Referer + Cookie headers — pass pageUrl to enable this.
///
/// HLS segments are concatenated into a raw MPEG-TS stream and then remuxed to MP4
/// with ffmpeg when available (same as yt-dlp); otherwise the .ts file is kept
/// (VLC/PotPlayer can play it, but it is not a real MP4 container).
/// </summary>
public class DownloadEngine
{
    /// <summary>HLS 里的一个分段。Offset/Length 为 0 表示"整文件"（普通 TS 列表）。</summary>
    public sealed record HlsSegment(string Url, long Offset, long Length);

    private readonly int _maxSegmentThreads;

    public DownloadEngine(int maxSegmentThreads = 8)
    {
        _maxSegmentThreads = Math.Max(1, maxSegmentThreads);
    }

    /// <summary>
    /// 两次收到数据之间允许的最长间隔。下载客户端刻意设成无限超时（大文件不能
    /// 被总时长掐断），但如果 CDN 半死（握手完成、不再发数据），任务会永远卡在
    /// IsActive、占着并发名额不放，只能靠用户手点取消 —— 所以补一个"读空闲超时"。
    /// </summary>
    public static TimeSpan ReadIdleTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 读一次数据，超过 <paramref name="idleTimeout"/> 没动静就抛 TimeoutException。
    /// 取消（ct 真的被取消）仍然走 OperationCanceledException，两者不能混。
    /// </summary>
    public static async Task<int> ReadWithIdleTimeoutAsync(
        Stream stream, Memory<byte> buffer, CancellationToken ct, TimeSpan? idleTimeout = null)
    {
        var timeout = idleTimeout ?? ReadIdleTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await stream.ReadAsync(buffer, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{timeout.TotalSeconds:0} 秒没有收到数据，连接可能已经断开");
        }
    }

    /// <summary>
    /// 拒绝 "200 + HTML 错误页"：token 过期/登录墙时站点常返回 200 的提示页，
    /// 以前会存成几 KB 的"视频"并写进去重记录，用户之后再也下不到正确文件。
    /// </summary>
    public static void EnsureLooksLikeMedia(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrEmpty(mediaType)) return;   // 有些 CDN 不返回类型，放行

        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"服务器返回的是 {mediaType} 而不是视频（可能是登录失效或错误页）");
        }
    }

    /// <summary>
    /// Decide whether a stream URL points to HLS content (shared with the queue
    /// so the output extension can be chosen before the download starts).
    /// </summary>
    public static bool IsHlsUrl(string streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl)) return false;

        // 只看路径/查询里的 .m3u8：早先还判了子串 "hls"，结果
        // "…/hls-off/movie.mp4" 或 "?hls=0" 会被当成播放列表，
        // 一次直链下载变成"解析 m3u8"，最后全是 404 或拼出垃圾文件。
        var path = streamUrl;
        var q = streamUrl.IndexOf('?', StringComparison.Ordinal);
        if (q > 0) path = streamUrl[..q];

        return path.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
               || streamUrl.Contains(".m3u8?", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Download a video from the given stream URL.
    /// Handles both HLS and direct URLs automatically.
    /// </summary>
    /// <param name="pageUrl">Original video page URL — used as Referer for CDN auth (PornHub etc.)</param>
    /// <returns>The final output path (may differ from outputPath when HLS was remuxed to MP4)</returns>
    public async Task<string> DownloadAsync(
        string streamUrl,
        string outputPath,
        string pageUrl = "",
        Action<double, string>? onProgress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (IsHlsUrl(streamUrl))
        {
            return await DownloadHlsAsync(streamUrl, outputPath, pageUrl, onProgress, ct);
        }

        await DownloadDirectAsync(streamUrl, outputPath, pageUrl, onProgress, ct);
        return outputPath;
    }

    // ─────────────────────────────────────────
    //  HLS / M3U8 download
    // ─────────────────────────────────────────

    private async Task<string> DownloadHlsAsync(
        string m3u8Url,
        string outputPath,
        string pageUrl,
        Action<double, string>? onProgress,
        CancellationToken ct)
    {
        var client = HttpClientFactory.GetDownloadClient();

        // 1. Fetch M3U8 playlist (with Referer/Cookie if pageUrl provided)
        var m3u8Content = await FetchTextAsync(client, m3u8Url, pageUrl, ct);
        var baseUri = new Uri(m3u8Url);

        // Check if this is a master playlist (points to sub-playlists)
        if (m3u8Content.Contains("#EXT-X-STREAM-INF"))
        {
            // Pick highest bandwidth sub-playlist
            var subUrl = ParseBestSubPlaylist(m3u8Content, baseUri);
            m3u8Content = await FetchTextAsync(client, subUrl, pageUrl, ct);
            baseUri = new Uri(subUrl);
        }

        // 2. 加密流先拒绝：下下来的是密文，与其给用户一个坏文件，不如说清楚
        if (IsEncrypted(m3u8Content))
            throw new NotSupportedException("该视频使用 AES-128 加密（#EXT-X-KEY），当前版本暂不支持");

        // 3. Parse segment URLs
        var segments = ParseSegmentInfos(m3u8Content, baseUri);
        if (segments.Count == 0)
            throw new InvalidDataException("No segments found in M3U8 playlist.");

        // fMP4/CMAF 需要先拼接初始化段（#EXT-X-MAP），否则整段都不解码
        var initSegment = ParseInitSegment(m3u8Content, baseUri);
        if (initSegment != null) segments.Insert(0, new HlsSegment(initSegment, 0, 0));

        // 3. Prepare temp directory for segments
        var tempDir = Path.Combine(Path.GetTempPath(), "videofetch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            int completed = 0;
            int total = segments.Count;

            using var semaphore = new SemaphoreSlim(_maxSegmentThreads);
            var downloadTasks = segments.Select(async (seg, index) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var segPath = Path.Combine(tempDir, $"seg_{index:D6}.ts");
                    await DownloadSegmentWithRetryAsync(client, seg, pageUrl, segPath, ct);

                    var done = Interlocked.Increment(ref completed);
                    var percent = (double)done / total * 100.0;
                    onProgress?.Invoke(percent, $"{done}/{total} segments");
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(downloadTasks);

            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke(99, "Merging segments...");

            // 4. Concatenate all .ts files into the final output
            await ConcatenateSegmentsAsync(tempDir, total, outputPath, ct);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        // 5. Decide the final container:
        //    - fMP4/CMAF segments concatenate into a valid fragmented MP4 → rename
        //    - MPEG-TS → remux with ffmpeg (bundled/PATH); keep .ts if unavailable
        var finalPath = await FinalizeHlsOutputAsync(outputPath, ct);
        // 收尾只报进度，不带"速度"文案：这个字符串会原样显示在队列的
        // 状态下面（曾经出现过一行英文 "Done"）
        onProgress?.Invoke(100, string.Empty);
        return finalPath;
    }

    /// <summary>
    /// Turn the concatenated HLS output into its proper container:
    /// MP4 ("ftyp" header, i.e. CMAF/fMP4 HLS) → rename to .mp4;
    /// MPEG-TS → remux to .mp4 via ffmpeg, keeping .ts only when ffmpeg is unavailable.
    /// </summary>
    private static async Task<string> FinalizeHlsOutputAsync(string outputPath, CancellationToken ct)
    {
        if (!outputPath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            return outputPath;

        var header = new byte[12];
        int read;
        await using (var fs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            read = await fs.ReadAsync(header.AsMemory(0, header.Length), ct);
            if (read < header.Length)
                return outputPath;
        }

        if (!IsFragmentedMp4Header(header))
            return await TryRemuxToMp4Async(outputPath, ct);

        var mp4Path = UniqueMp4Path(outputPath);
        File.Move(outputPath, mp4Path);
        return mp4Path;
    }

    /// <summary>
    /// 拼接后的内容是不是 fMP4/CMAF：第 4-7 字节是 "ftyp" box。
    /// 是的话直接改名 .mp4 就行，MPEG-TS 才需要 ffmpeg 重封装。
    /// </summary>
    public static bool IsFragmentedMp4Header(ReadOnlySpan<byte> header)
        => header.Length >= 8
           && header[4] == (byte)'f' && header[5] == (byte)'t'
           && header[6] == (byte)'y' && header[7] == (byte)'p';

    /// <summary>
    /// Pick a non-existing ".mp4" name matching the given .ts path.
    /// </summary>
    public static string UniqueMp4Path(string tsPath)
    {
        var mp4Path = Path.ChangeExtension(tsPath, ".mp4");
        if (!File.Exists(mp4Path)) return mp4Path;

        var dir = Path.GetDirectoryName(mp4Path)!;
        var name = Path.GetFileNameWithoutExtension(mp4Path);
        for (int i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}).mp4");
            if (!File.Exists(candidate)) return candidate;
        }
        return mp4Path;
    }

    /// <summary>
    /// Remux a raw .ts stream into a proper .mp4 container using ffmpeg
    /// (stream copy, no re-encode). Returns the original path when ffmpeg
    /// is unavailable or the remux fails.
    /// </summary>
    private static async Task<string> TryRemuxToMp4Async(string tsPath, CancellationToken ct)
    {
        if (!tsPath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)) return tsPath;

        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return tsPath;

        var mp4Path = UniqueMp4Path(tsPath);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-y -loglevel error -i \"{tsPath}\" -c copy -movflags +faststart \"{mp4Path}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return tsPath;

            _ = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || !File.Exists(mp4Path) || new FileInfo(mp4Path).Length == 0)
            {
                try { File.Delete(mp4Path); } catch { }
                return tsPath;
            }

            File.Delete(tsPath);
            return mp4Path;
        }
        catch
        {
            try { File.Delete(mp4Path); } catch { }
            return tsPath;
        }
    }

    /// <summary>
    /// Locate ffmpeg: next to the executable first, then on PATH.
    /// </summary>
    private static string? FindFfmpeg()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
        return pathDirs
            .Select(dir => Path.Combine(dir, "ffmpeg.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Fetch text content, adding Referer + Cookie when pageUrl is provided
    /// (required for PornHub CDN authentication).
    /// </summary>
    private static async Task<string> FetchTextAsync(
        HttpClient client, string url, string pageUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pageUrl))
            return await client.GetStringAsync(url, ct);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // 用 TryAdd 而不是 Add：pageUrl 含非 ASCII（带中文的 key）时 Add 会抛 FormatException
        req.Headers.TryAddWithoutValidation("Referer", pageUrl);
        // Cookie 走 HttpClientFactory 的 CookieContainer（含用户导入的），不要在请求头上硬写
        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// 从主播放列表里挑码率最高的子播放列表。
    /// 公开是为了让离线测试能直接喂一段 m3u8 文本。
    /// </summary>
    public static string ParseBestSubPlaylist(string m3u8, Uri baseUri)
    {
        string? bestUrl = null;
        long bestBw = -1;
        var lines = m3u8.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF"))
            {
                var bwMatch = Regex.Match(line, @"BANDWIDTH=(\d+)");
                var bw = bwMatch.Success ? long.Parse(bwMatch.Groups[1].Value) : 0;
                if (i + 1 < lines.Length)
                {
                    var urlLine = lines[i + 1].Trim();
                    if (!urlLine.StartsWith("#") && (bw > bestBw || bestUrl == null))
                    {
                        bestBw = bw;
                        bestUrl = urlLine.StartsWith("http") ? urlLine : new Uri(baseUri, urlLine).ToString();
                    }
                }
            }
        }

        return bestUrl ?? throw new InvalidDataException("No sub-playlist found in master M3U8.");
    }

    /// <summary>
    /// 媒体播放列表 → 分段地址（相对地址按播放列表地址补全）。
    /// 公开是为了让离线测试能直接喂一段 m3u8 文本。
    /// </summary>
    public static List<string> ParseSegments(string m3u8, Uri baseUri)
        => ParseSegmentInfos(m3u8, baseUri).Select(s => s.Url).ToList();

    /// <summary>
    /// 解析媒体播放列表，保留 #EXT-X-BYTERANGE 的 (offset, length)。
    ///
    /// 以前只收集 URL 行、把 BYTERANGE 当注释丢掉：用字节范围描述的 fMP4/CMAF
    /// 列表里同一个 URI 会出现多次，每次都下整文件，拼出来必然花屏。
    /// </summary>
    public static List<HlsSegment> ParseSegmentInfos(string m3u8, Uri baseUri)
    {
        var segments = new List<HlsSegment>();
        long pendingLength = -1;
        long pendingOffset = -1;
        string? lastUrl = null;
        long lastEnd = 0;

        foreach (var raw in m3u8.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-BYTERANGE", StringComparison.OrdinalIgnoreCase))
            {
                // 形如 #EXT-X-BYTERANGE:1000@0；省略 @offset 表示"接上一段末尾"
                var range = Regex.Match(line, @":(\d+)(?:@(\d+))?");
                if (range.Success)
                {
                    pendingLength = long.Parse(range.Groups[1].Value);
                    pendingOffset = range.Groups[2].Success
                        ? long.Parse(range.Groups[2].Value)
                        : -1;
                }
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal)) continue;

            var url = line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? line
                : new Uri(baseUri, line).ToString();

            if (pendingLength >= 0)
            {
                var offset = pendingOffset >= 0
                    ? pendingOffset
                    : (string.Equals(lastUrl, url, StringComparison.Ordinal) ? lastEnd : 0);

                segments.Add(new HlsSegment(url, offset, pendingLength));
                lastEnd = offset + pendingLength;
                lastUrl = url;
                pendingLength = -1;
                pendingOffset = -1;
                continue;
            }

            segments.Add(new HlsSegment(url, 0, 0));
            lastUrl = url;
            lastEnd = 0;
        }

        return segments;
    }

    /// <summary>
    /// fMP4/CMAF 的初始化段：<c>#EXT-X-MAP:URI="init.mp4"</c>（可能带 BYTERANGE）。
    /// 这个段必须拼在最前面，否则后面的 moof 分片没有 moov，整段都解不出来。
    /// 普通 MPEG-TS 列表没有它，返回 null。
    /// </summary>
    public static string? ParseInitSegment(string m3u8, Uri baseUri)
    {
        // 注意别在 verbatim 字符串里写 \n：要写成 [^\r\n] 或干脆避开换行类
        var match = Regex.Match(m3u8, @"#EXT-X-MAP:[^""]*URI=""([^""]+)""", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var uri = match.Groups[1].Value.Trim();
        if (uri.Length == 0) return null;

        return uri.StartsWith("http") ? uri : new Uri(baseUri, uri).ToString();
    }

    /// <summary>
    /// 是否 AES-128 加密流。加密段下下来是密文，目前不支持解密，
    /// 提前失败并给出明确原因，比给用户一个打不开的文件要好。
    /// </summary>
    public static bool IsEncrypted(string m3u8)
        => Regex.IsMatch(m3u8, @"#EXT-X-KEY:\s*METHOD=(?!NONE)[A-Z0-9\-]+", RegexOptions.IgnoreCase);

    private static async Task DownloadSegmentWithRetryAsync(
        HttpClient client, HlsSegment segment, string pageUrl, string path,
        CancellationToken ct, int retries = 3)
    {
        for (int attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                // 单段也要有超时：否则一个卡死的分段会让 Task.WhenAll 永远等下去
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(SegmentTimeout);

                using var req = new HttpRequestMessage(HttpMethod.Get, segment.Url);
                if (!string.IsNullOrEmpty(pageUrl))
                    req.Headers.TryAddWithoutValidation("Referer", pageUrl);

                // 字节范围分段：不带 Range 就会把整个文件当一个分段重复下载
                if (segment.Length > 0)
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
                        segment.Offset, segment.Offset + segment.Length - 1);

                using var resp = await client.SendAsync(req, attemptCts.Token);
                EnsureLooksLikeMedia(resp);
                var bytes = await resp.Content.ReadAsByteArrayAsync(attemptCts.Token);

                // 服务端忽略了 Range（返回整文件）时不要写脏数据
                if (segment.Length > 0 && bytes.LongLength > segment.Length)
                    throw new InvalidDataException(
                        $"分段返回 {bytes.LongLength} 字节，超过请求的范围 {segment.Length}");

                await File.WriteAllBytesAsync(path, bytes, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // 用户取消：不要再重试
            }
            catch when (attempt < retries - 1)
            {
                await Task.Delay(500 * (attempt + 1), ct);
            }
        }
    }

    /// <summary>单个分段的超时（含建连与传输）。</summary>
    private static readonly TimeSpan SegmentTimeout = TimeSpan.FromSeconds(60);

    private static async Task ConcatenateSegmentsAsync(
        string tempDir, int totalSegments, string outputPath, CancellationToken ct)
    {
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true);

        for (int i = 0; i < totalSegments; i++)
        {
            ct.ThrowIfCancellationRequested();
            var segPath = Path.Combine(tempDir, $"seg_{i:D6}.ts");
            if (!File.Exists(segPath)) continue;

            await using var seg = new FileStream(segPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
            await seg.CopyToAsync(output, ct);
        }
    }

    // ─────────────────────────────────────────
    //  Direct file download
    // ─────────────────────────────────────────

    private static async Task DownloadDirectAsync(
        string url,
        string outputPath,
        string pageUrl,
        Action<double, string>? onProgress,
        CancellationToken ct)
    {
        var client = HttpClientFactory.GetDownloadClient();

        HttpRequestMessage? req = null;
        if (!string.IsNullOrEmpty(pageUrl))
        {
            req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", pageUrl);
            // Cookie 走 HttpClientFactory 的 CookieContainer（含用户导入的），不要在请求头上硬写
        }

        using (req)
        using (var response = req != null
            ? await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            : await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            EnsureLooksLikeMedia(response);

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 64 * 1024, useAsync: true);

            var buffer = new byte[64 * 1024];
            long downloaded = 0;
            int read;
            var sw = Stopwatch.StartNew();
            long lastBytes = 0;

            while ((read = await ReadWithIdleTimeoutAsync(stream, buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;

                if (sw.ElapsedMilliseconds > 500)
                {
                    var speedBps = (downloaded - lastBytes) / (sw.Elapsed.TotalSeconds);
                    lastBytes = downloaded;
                    sw.Restart();
                    var speedStr = speedBps > 1_000_000
                        ? $"{speedBps / 1_000_000.0:F1} MB/s"
                        : $"{speedBps / 1_000.0:F0} KB/s";

                    // 封顶 100：Content-Length 与实际字节数不符时（分块/代理改写）
                    // 会出现 120% 这种值，队列那边看到 >=100 就把状态写成"已完成"，
                    // 于是同一行既显示已完成又还挂着"取消"按钮。
                    var percent = totalBytes > 0
                        ? Math.Min(100.0, (double)downloaded / totalBytes * 100.0)
                        : -1;
                    onProgress?.Invoke(percent, speedStr);
                }
            }
        }

        // 收尾只报进度，不带"速度"文案：这个字符串会原样显示在队列的
        // 状态下面（曾经出现过一行英文 "Done"）
        onProgress?.Invoke(100, string.Empty);
    }
}

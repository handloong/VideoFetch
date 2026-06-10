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
/// </summary>
public class DownloadEngine
{
    private readonly int _maxSegmentThreads;

    public DownloadEngine(int maxSegmentThreads = 8)
    {
        _maxSegmentThreads = maxSegmentThreads;
    }

    /// <summary>
    /// Download a video from the given stream URL to outputPath.
    /// Handles both HLS and direct URLs automatically.
    /// </summary>
    /// <param name="pageUrl">Original video page URL — used as Referer for CDN auth (PornHub etc.)</param>
    public async Task DownloadAsync(
        string streamUrl,
        string outputPath,
        string pageUrl = "",
        Action<double, string>? onProgress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (streamUrl.Contains(".m3u8") || streamUrl.Contains("hls"))
        {
            await DownloadHlsAsync(streamUrl, outputPath, pageUrl, onProgress, ct);
        }
        else
        {
            await DownloadDirectAsync(streamUrl, outputPath, pageUrl, onProgress, ct);
        }
    }

    // ─────────────────────────────────────────
    //  HLS / M3U8 download
    // ─────────────────────────────────────────

    private async Task DownloadHlsAsync(
        string m3u8Url,
        string outputPath,
        string pageUrl,
        Action<double, string>? onProgress,
        CancellationToken ct)
    {
        var client = HttpClientFactory.GetClient();

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

        // 2. Parse segment URLs
        var segments = ParseSegments(m3u8Content, baseUri);
        if (segments.Count == 0)
            throw new InvalidDataException("No segments found in M3U8 playlist.");

        // 3. Prepare temp directory for segments
        var tempDir = Path.Combine(Path.GetTempPath(), "videofetch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            int completed = 0;
            int total = segments.Count;

            using var semaphore = new SemaphoreSlim(_maxSegmentThreads);
            var downloadTasks = segments.Select(async (segUrl, index) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var segPath = Path.Combine(tempDir, $"seg_{index:D6}.ts");
                    await DownloadSegmentWithRetryAsync(client, segUrl, pageUrl, segPath, ct);

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

            // 4. Concatenate all .ts files into final output
            await ConcatenateSegmentsAsync(tempDir, total, outputPath, ct);

            onProgress?.Invoke(100, "Done");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
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

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Referer", pageUrl);
        req.Headers.Add("Cookie", "platform=pc; ageconfirmed=1; age_verified=1");
        var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static string ParseBestSubPlaylist(string m3u8, Uri baseUri)
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

    private static List<string> ParseSegments(string m3u8, Uri baseUri)
    {
        var segments = new List<string>();
        foreach (var raw in m3u8.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("#") || string.IsNullOrEmpty(line)) continue;
            var url = line.StartsWith("http") ? line : new Uri(baseUri, line).ToString();
            segments.Add(url);
        }
        return segments;
    }

    private static async Task DownloadSegmentWithRetryAsync(
        HttpClient client, string url, string pageUrl, string path, CancellationToken ct, int retries = 3)
    {
        for (int attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                byte[] bytes;
                if (!string.IsNullOrEmpty(pageUrl))
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Add("Referer", pageUrl);
                    req.Headers.Add("Cookie", "platform=pc; ageconfirmed=1; age_verified=1");
                    var resp = await client.SendAsync(req, ct);
                    resp.EnsureSuccessStatusCode();
                    bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                }
                else
                {
                    bytes = await client.GetByteArrayAsync(url, ct);
                }
                await File.WriteAllBytesAsync(path, bytes, ct);
                return;
            }
            catch when (attempt < retries - 1 && !ct.IsCancellationRequested)
            {
                await Task.Delay(500 * (attempt + 1), ct);
            }
        }
    }

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
        var client = HttpClientFactory.GetClient();
        HttpResponseMessage response;

        if (!string.IsNullOrEmpty(pageUrl))
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", pageUrl);
            req.Headers.Add("Cookie", "platform=pc; ageconfirmed=1; age_verified=1");
            response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        else
        {
            response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 64 * 1024, useAsync: true);

        var buffer = new byte[64 * 1024];
        long downloaded = 0;
        int read;
        var sw = Stopwatch.StartNew();
        long lastBytes = 0;

        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
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

                var percent = totalBytes > 0 ? (double)downloaded / totalBytes * 100.0 : -1;
                onProgress?.Invoke(percent, speedStr);
            }
        }

        onProgress?.Invoke(100, "Done");
        response.Dispose();
    }
}

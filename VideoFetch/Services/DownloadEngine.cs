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
    private readonly int _maxSegmentThreads;

    public DownloadEngine(int maxSegmentThreads = 8)
    {
        _maxSegmentThreads = Math.Max(1, maxSegmentThreads);
    }

    /// <summary>
    /// Decide whether a stream URL points to HLS content (shared with the queue
    /// so the output extension can be chosen before the download starts).
    /// </summary>
    public static bool IsHlsUrl(string streamUrl)
        => streamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
           || streamUrl.Contains("hls", StringComparison.OrdinalIgnoreCase);

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
        onProgress?.Invoke(100, "Done");
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
        await using (var fs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (await fs.ReadAsync(header.AsMemory(0, header.Length), ct) < header.Length)
                return outputPath;
        }

        // MP4 boxes start with a 4-byte size followed by the box type
        var isMp4 = header[4] == (byte)'f' && header[5] == (byte)'t'
                    && header[6] == (byte)'p' && header[7] == (byte)'y';
        if (!isMp4)
            return await TryRemuxToMp4Async(outputPath, ct);

        var mp4Path = UniqueMp4Path(outputPath);
        File.Move(outputPath, mp4Path);
        return mp4Path;
    }

    /// <summary>
    /// Pick a non-existing ".mp4" name matching the given .ts path.
    /// </summary>
    private static string UniqueMp4Path(string tsPath)
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
        var client = HttpClientFactory.GetDownloadClient();

        HttpRequestMessage? req = null;
        if (!string.IsNullOrEmpty(pageUrl))
        {
            req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", pageUrl);
            req.Headers.Add("Cookie", "platform=pc; ageconfirmed=1; age_verified=1");
        }

        using (req)
        using (var response = req != null
            ? await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            : await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
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
        }

        onProgress?.Invoke(100, "Done");
    }
}

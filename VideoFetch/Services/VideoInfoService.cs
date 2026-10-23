using VideoFetch.Models;
using VideoFetch.Services.Sites;
using System.Net.Http;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace VideoFetch.Services;

/// <summary>
/// Routes a URL to the appropriate site parser and returns video info.
/// Also handles resolving the final download URL (including PornHub mp4_api),
/// and provides search/category browsing functionality.
/// </summary>
public class VideoInfoService : IVideoInfoProvider
{
    private readonly List<ISiteParser> _parsers = new()
    {
        new PornHubParser(),
        new XVideosParser(),
        new XNxxParser(),
        new PinSeParser(),
    };

    public bool IsSupported(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return _parsers.Any(p => p.CanHandle(url));
    }

    public SiteType DetectSite(string url)
    {
        if (url.Contains("pornhub.com") || url.Contains("pornhubpremium.com")) return SiteType.PornHub;
        if (url.Contains("xvideos.com")) return SiteType.XVideos;
        if (url.Contains("xnxx.com")) return SiteType.XNxx;
        if (url.Contains("91pinse.com")) return SiteType.PinSe;
        return SiteType.Unknown;
    }

    /// <summary>
    /// Get parser instance by SiteType
    /// </summary>
    public ISiteParser? GetParser(SiteType site) => site switch
    {
        SiteType.PornHub => _parsers.OfType<PornHubParser>().FirstOrDefault(),
        SiteType.XVideos => _parsers.OfType<XVideosParser>().FirstOrDefault(),
        SiteType.XNxx => _parsers.OfType<XNxxParser>().FirstOrDefault(),
        SiteType.PinSe => _parsers.OfType<PinSeParser>().FirstOrDefault(),
        _ => null
    };

    // ── VIDEO INFO ──────────────────────────────────────────────

    public async Task<VideoInfo> FetchAsync(string url, CancellationToken ct = default)
    {
        url = url.Trim();

        var parser = _parsers.FirstOrDefault(p => p.CanHandle(url))
            ?? throw new NotSupportedException($"No parser found for URL: {url}");

        var info = await parser.FetchVideoInfoAsync(url, ct)
            ?? throw new InvalidOperationException(
                $"无法获取视频信息，请检查链接是否有效以及网络是否可达（{url}）");

        // Fallback title
        if (string.IsNullOrEmpty(info.Title))
            info.Title = $"video_{DateTime.Now:yyyyMMdd_HHmmss}";

        // 按清晰度从高到低排好：界面下拉框按这个顺序展示，批量下载取的
        // Qualities[0] 也因此真的是最高画质（顺序原本取决于站点 HTML 的
        // 书写顺序，经常是低清在前）。
        info.Qualities.Sort((a, b) =>
            ParseQualityHeuristic(b.Value).CompareTo(ParseQualityHeuristic(a.Value)));

        // Add "best/worst" pseudo-options if we have multiple qualities
        if (info.Qualities.Count > 1)
        {
            info.Qualities.Insert(0, new QualityOption("Best available", "best"));
            info.Qualities.Add(new QualityOption("Worst available", "worst"));
        }

        return info;
    }

    // ── SEARCH ─────────────────────────────────────────────────

    /// <summary>
    /// Search videos on a specific site by keyword query.
    /// </summary>
    public async Task<List<SearchResult>> SearchAsync(SiteType site, string query, int page = 1, CancellationToken ct = default)
    {
        var parser = GetParser(site);
        if (parser == null)
            throw new NotSupportedException($"Search not supported for site: {site}");
        return await parser.SearchAsync(query, page, ct);
    }

    // ── CATEGORIES ─────────────────────────────────────────────

    /// <summary>
    /// List categories/tags of a site (live parse with built-in fallback).
    /// </summary>
    public async Task<List<CategoryItem>> GetCategoriesAsync(SiteType site, CancellationToken ct = default)
    {
        var parser = GetParser(site)
            ?? throw new NotSupportedException($"Categories not supported for site: {site}");
        return await parser.GetCategoriesAsync(ct);
    }

    /// <summary>
    /// Browse videos of a category (key from GetCategoriesAsync).
    /// </summary>
    public async Task<List<SearchResult>> BrowseCategoryAsync(
        SiteType site, string categoryKey, int page = 1, CancellationToken ct = default)
    {
        var parser = GetParser(site)
            ?? throw new NotSupportedException($"Categories not supported for site: {site}");
        return await parser.BrowseCategoryAsync(categoryKey, page, ct);
    }

    // ── STREAM RESOLUTION ─────────────────────────────────────

    /// <summary>
    /// Pure (no network) resolution of "best"/"worst"/label keys to the concrete
    /// StreamUrls key. Used by the queue to pick the output file extension before
    /// downloading, and by ResolveStreamUrlAsync to pick the actual URL.
    /// </summary>
    public static string ResolveQualityKey(VideoInfo info, string qualityKey)
    {
        var keys = info.StreamUrls.Keys.ToList();
        if (keys.Count == 0) return qualityKey;

        if (qualityKey != "best" && qualityKey != "worst")
        {
            return keys.Contains(qualityKey)
                ? qualityKey
                : keys.FirstOrDefault(k => k != "best" && k != "worst") ?? keys[0];
        }

        // "best" / "worst": rank candidates by quality heuristic
        var candidates = keys.Where(k => k != "mp4_api" && k != "best" && k != "worst").ToList();
        if (candidates.Count == 0) candidates = keys;
        return qualityKey == "best"
            ? candidates.OrderByDescending(ParseQualityHeuristic).First()
            : candidates.OrderBy(ParseQualityHeuristic).First();
    }

    /// <summary>
    /// Resolve the actual download URL for a given quality key.
    /// For PornHub mp4_*: calls the /video/get_media API to get signed direct MP4 URLs.
    /// For HLS: returns the m3u8 URL as-is (DownloadEngine handles HLS).
    /// For direct URLs: returns as-is.
    /// </summary>
    public async Task<string> ResolveStreamUrlAsync(
        VideoInfo info, string qualityKey, CancellationToken ct = default)
    {
        var key = ResolveQualityKey(info, qualityKey);

        if (!info.StreamUrls.TryGetValue(key, out var url))
        {
            var fallback = info.StreamUrls.Values.FirstOrDefault();
            if (fallback == null)
                throw new InvalidOperationException("没有可用的视频流地址");
            return fallback;
        }

        // PornHub: resolve mp4_* keys by calling the get_media API
        if (info.Site == SiteType.PornHub && key.StartsWith("mp4_"))
        {
            var preferred = key.Length > 4 ? key[4..] : ""; // e.g. "720" from "mp4_720"
            return await CallPornHubGetMediaApiAsync(url, info.Url, ct, preferred);
        }

        // If the URL is a PornHub get_media API url, resolve it
        if (url.Contains("/get_media"))
            return await CallPornHubGetMediaApiAsync(url, info.Url, ct);

        return url;
    }

    /// <summary>
    /// Call PornHub's /video/get_media?s=... API to get time-limited signed MP4 direct URLs.
    /// Returns the URL for <paramref name="preferredQuality"/> (e.g. "720") when available,
    /// otherwise the highest quality.
    /// </summary>
    private static async Task<string> CallPornHubGetMediaApiAsync(
        string apiUrl, string pageUrl, CancellationToken ct, string preferredQuality = "")
    {
        var client = HttpClientFactory.GetClient();
        var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.Add("Referer", pageUrl);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        req.Headers.Add("Cookie", "platform=pc; ageconfirmed=1; age_verified=1");

        var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        var arr = JArray.Parse(json);

        // Build quality → url map from API response
        var mp4Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in arr)
        {
            var qual = item["quality"]?.ToString() ?? "";
            var vUrl = item["videoUrl"]?.ToString() ?? "";
            if (!string.IsNullOrEmpty(qual) && !string.IsNullOrEmpty(vUrl))
                mp4Map[$"{qual}p"] = vUrl;
        }

        if (mp4Map.Count == 0)
            throw new InvalidOperationException("PornHub mp4 API returned no usable URLs.");

        // Prefer the quality the user actually selected (e.g. "720" → "720p")
        if (!string.IsNullOrEmpty(preferredQuality))
        {
            var digits = Regex.Match(preferredQuality, @"\d{3,4}").Value;
            if (digits.Length > 0)
            {
                var match = mp4Map.FirstOrDefault(kv =>
                    Regex.Match(kv.Key, @"\d{3,4}").Value == digits);
                if (match.Value != null) return match.Value;
            }
        }

        // Fallback: highest quality available
        return mp4Map
            .OrderByDescending(kv => ParseQualityHeuristic(kv.Key))
            .First().Value;
    }

    private static int ParseQualityHeuristic(string key)
    {
        // Extract numeric quality from keys like "1080p", "hls_1080", "mp4_720"
        var m = Regex.Match(key, @"(\d{3,4})");
        if (m.Success) return int.Parse(m.Groups[1].Value);

        // Non-numeric keys. These must stay BELOW real resolutions, otherwise
        // a literal "high" (which on XVideos is only the 360p mp4) outranks
        // "hls_720" and "best" silently downloads the low-resolution file.
        return key.ToLowerInvariant() switch
        {
            var k when k.Contains("best") => int.MaxValue,
            var k when k.Contains("hls") => 720,    // master playlist, usually up to 720p+
            var k when k.Contains("high") || k.Contains("direct") => 480,
            var k when k.Contains("low") => 240,
            _ => 0
        };
    }
}

using VideoFetch.Models;
using VideoFetch.Services.Sites;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace VideoFetch.Services;

/// <summary>
/// Routes a URL to the appropriate site parser and returns video info.
/// Also handles resolving the final download URL (including PornHub mp4_api),
/// and provides search/category browsing functionality.
/// </summary>
public class VideoInfoService
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

        var info = await parser.FetchVideoInfoAsync(url, ct);

        // Fallback title
        if (string.IsNullOrEmpty(info.Title))
            info.Title = $"video_{DateTime.Now:yyyyMMdd_HHmmss}";

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

    // ── STREAM RESOLUTION ─────────────────────────────────────

    /// <summary>
    /// Resolve the actual download URL for a given quality key.
    /// For PornHub mp4_*: calls the /video/get_media API to get signed direct MP4 URLs.
    /// For HLS: returns the m3u8 URL as-is (DownloadEngine handles HLS).
    /// For direct URLs: returns as-is.
    /// </summary>
    public async Task<string> ResolveStreamUrlAsync(
        VideoInfo info, string qualityKey, CancellationToken ct = default)
    {
        // PornHub: resolve mp4_* keys by calling the get_media API
        if (info.Site == SiteType.PornHub && qualityKey.StartsWith("mp4_"))
        {
            return await ResolvePornHubMp4ApiAsync(info, qualityKey, ct);
        }

        // Generic "best" / "worst"
        if (qualityKey == "best")
        {
            var best = info.StreamUrls.Keys
                .Where(k => k != "mp4_api" && k != "best" && k != "worst")
                .OrderByDescending(k => ParseQualityHeuristic(k))
                .FirstOrDefault() ?? info.StreamUrls.Keys.First();
            return info.StreamUrls[best];
        }

        if (qualityKey == "worst")
        {
            var worst = info.StreamUrls.Keys
                .Where(k => k != "mp4_api" && k != "best" && k != "worst")
                .OrderBy(k => ParseQualityHeuristic(k))
                .FirstOrDefault() ?? info.StreamUrls.Keys.Last();
            return info.StreamUrls[worst];
        }

        // Direct lookup
        if (info.StreamUrls.TryGetValue(qualityKey, out var url))
        {
            // If the URL is a PornHub get_media API url, resolve it
            if (url.Contains("/get_media"))
                return await CallPornHubGetMediaApiAsync(url, info.Url, ct);
            return url;
        }

        // Fallback: return first available URL
        return info.StreamUrls.Values.First();
    }

    /// <summary>
    /// Call PornHub's /video/get_media?s=... API to get time-limited signed MP4 direct URLs.
    /// </summary>
    private static async Task<string> ResolvePornHubMp4ApiAsync(
        VideoInfo info, string qualityKey, CancellationToken ct)
    {
        // Find the API URL — it's stored under the "mp4_*" key in StreamUrls
        if (!info.StreamUrls.TryGetValue(qualityKey, out var apiUrl))
            throw new InvalidOperationException($"No stream URL found for key: {qualityKey}");

        return await CallPornHubGetMediaApiAsync(apiUrl, info.Url, ct);
    }

    private static async Task<string> CallPornHubGetMediaApiAsync(
        string apiUrl, string pageUrl, CancellationToken ct)
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

        // Also update VideoInfo.StreamUrls with the fresh signed URLs (side effect on info is not possible here
        // since info is not passed — caller should do this)

        // Return the highest quality URL by default
        return mp4Map
            .OrderByDescending(kv => ParseQualityHeuristic(kv.Key))
            .First().Value;
    }

    private static int ParseQualityHeuristic(string key)
    {
        // Extract numeric quality from keys like "1080p", "hls_1080", "mp4_720"
        var m = System.Text.RegularExpressions.Regex.Match(key, @"(\d{3,4})");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
}

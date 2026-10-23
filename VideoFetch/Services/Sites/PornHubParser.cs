using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VideoFetch.Models;

namespace VideoFetch.Services.Sites;

public class PornHubParser : ISiteParser
{
    public SiteType Site => SiteType.PornHub;
    // Get the shared client per call so a proxy change (HttpClientFactory.Configure) takes effect

    public bool CanHandle(string url)
        => !string.IsNullOrWhiteSpace(url) && url.Contains("pornhub.com");

    public async Task<VideoInfo?> FetchVideoInfoAsync(string url, CancellationToken ct = default)
    {
        var info = new VideoInfo { Url = url, Site = Site };
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        // Cookie 走 HttpClientFactory 的 CookieContainer（含用户导入的），不要在请求头上硬写
        req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        try
        {
            var resp = await HttpClientFactory.GetClient().SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync(ct);

            // Title
            var m = Regex.Match(html, @"""video_title""\s*:\s*""([^""]+)""");
            if (!m.Success) m = Regex.Match(html, @"<title>([^<]+)");
            // 先解 \uXXXX 转义再解 HTML 实体：站点会把标题二次转义，
            // 顺序反了会把形如 &comma; 的字面量直接送到界面上。
            if (m.Success) info.Title = ParserHelper.DecodeEntities(DecodeJsonString(m.Groups[1].Value.Trim()));

            // Author
            m = Regex.Match(html, @"""author""\s*:\s*\{[^}]*?""name""\s*:\s*""([^""]+)""");
            if (!m.Success) m = Regex.Match(html, @"class=""usernameBadgesWrapper[^""]*""[^>]*>[^<]*<[^>]+>([^<]+)<");
            if (m.Success) info.Author = ParserHelper.DecodeEntities(DecodeJsonString(m.Groups[1].Value.Trim()));

            // Duration
            m = Regex.Match(html, @"""duration""\s*:\s*""([^""]+)""");
            if (m.Success) info.Duration = ParseIso8601Duration(m.Groups[1].Value);

            // Views
            m = Regex.Match(html, @"""interactionCount""\s*:\s*""([^""]+)""");
            if (m.Success) info.ViewCount = FormatNumber(m.Groups[1].Value);

            // Thumbnail
            m = Regex.Match(html, @"""thumbnailUrl""\s*:\s*\[""([^""]+)""");
            if (!m.Success) m = Regex.Match(html, @"""thumbnailUrl""\s*:\s*""([^""]+)""");
            if (m.Success) info.ThumbnailUrl = m.Groups[1].Value;

            ExtractStreams(html, info);
            return info;
        }
        catch { return null; }
    }

    private static void ExtractStreams(string html, VideoInfo info)
    {
        var flashVarsMatch = Regex.Match(html, @"var\s+flashvars_\w+\s*=\s*(\{.*?\});", RegexOptions.Singleline);
        if (!flashVarsMatch.Success) return;

        var json = JObject.Parse(flashVarsMatch.Groups[1].Value);
        var mediaDefs = json["mediaDefinitions"] as JArray;
        if (mediaDefs == null) return;

        foreach (JObject def in mediaDefs)
        {
            var format = def["format"]?.ToString() ?? "";
            var quality = ReadScalar(def["quality"]);
            var videoUrl = def["videoUrl"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(videoUrl)) continue;

            // 站点偶尔给出 "quality": []（空数组），ToString() 会得到 "[]"，
            // key 变成 mp4_[]、画质下拉框里出现 "[]p (MP4)" 这种脏项。
            var q = string.IsNullOrEmpty(quality) ? "auto" : quality;
            var label = string.IsNullOrEmpty(quality) ? "auto" : $"{quality}p";

            if (format == "hls")
            {
                info.StreamUrls[$"hls_{q}"] = videoUrl;
                info.Qualities.Add(new QualityOption($"{label} (HLS)", $"hls_{q}"));
            }
            else if (format == "mp4" && videoUrl.Contains("/video/get_media"))
            {
                info.StreamUrls[$"mp4_{q}"] = videoUrl;
                info.Qualities.Add(new QualityOption($"{label} (MP4)", $"mp4_{q}"));
            }
        }
    }

    /// <summary>只认标量值（字符串/数字）；数组、对象、null 一律当空。</summary>
    private static string ReadScalar(JToken? token)
        => token is JValue { Value: not null } value ? value.ToString() : "";

    public async Task<List<SearchResult>> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        var searchUrl = $"https://www.pornhub.com/video/search?search={Uri.EscapeDataString(query)}&page={page}";
        var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        // Cookie 走 HttpClientFactory 的 CookieContainer（含用户导入的），不要在请求头上硬写
        req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        try
        {
            var resp = await HttpClientFactory.GetClient().SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return results;
            var html = await resp.Content.ReadAsStringAsync(ct);
            return ParseSearchResultsHtml(html);
        }
        catch { return results; }
    }

    public static List<SearchResult> ParseSearchResultsHtml(string html)
    {
        var results = new List<SearchResult>();
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        var items = doc.DocumentNode.SelectNodes("//li[contains(@class,'pcVideoListItem')]")
                    ?? doc.DocumentNode.SelectNodes("//li[contains(@class,'videoBox')]");

        if (items == null) return results;

        foreach (var item in items)
        {
            var linkNode = item.SelectSingleNode(".//a[contains(@href,'/view_video.php')]")
                            ?? item.SelectSingleNode(".//a[contains(@href,'phvideos')]");
            if (linkNode == null) continue;

            var href = linkNode.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(href)) continue;
            var url = href.StartsWith("http") ? href : "https://www.pornhub.com" + href;

            var titleNode = item.SelectSingleNode(".//span[contains(@class,'title')]//a")
                            ?? linkNode;
            var title = ParserHelper.DecodeEntities(DecodeJsonString(titleNode.InnerText?.Trim() ?? ""));

            var durNode = item.SelectSingleNode(".//span[contains(@class,'duration')]")
                            ?? item.SelectSingleNode(".//div[contains(@class,'duration')]");
            var duration = durNode?.InnerText?.Trim() ?? "";

            var authorNode = item.SelectSingleNode(".//span[contains(@class,'username')]//a")
                              ?? item.SelectSingleNode(".//a[contains(@class,'username')]");
            var author = ParserHelper.DecodeEntities(authorNode?.InnerText?.Trim() ?? "");

            var imgNode = item.SelectSingleNode(".//img");
            var thumb = ParserHelper.GetThumbUrl(imgNode);

            results.Add(new SearchResult
            {
                Title = title,
                Url = url,
                Author = author,
                Duration = duration,
                ThumbnailUrl = thumb,
                Site = SiteType.PornHub
            });
        }
        return results;
    }

    // ═══════════════════════════════════════════════════════
    //  CATEGORIES
    // ═══════════════════════════════════════════════════════

    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
    /// <summary>Category page URL for a key from GetCategoriesAsync.</summary>
    public static string BuildCategoryUrl(string categoryKey, int page = 1)
        => CategoryParsing.BuildPornHubUrl(categoryKey, page);

    /// <summary>
    /// List PornHub categories from the live /categories grid
    /// (li.catPic, ~115 entries with real names + video counts).
    /// Returns an empty list when the page is unreachable — the caller shows
    /// "load failed" instead of inventing category names.
    /// </summary>
    public async Task<List<CategoryItem>> GetCategoriesAsync(CancellationToken ct = default)
    {
        try
        {
            var html = await GetHtmlAsync("https://www.pornhub.com/categories", ct);
            return CategoryParsing.ParsePornHub(html);
        }
        catch
        {
            return new List<CategoryItem>();
        }
    }

    /// <summary>
    /// Browse videos of a PornHub category (/video?c={id}&amp;page=N).
    /// Category listing pages reuse the search markup.
    /// </summary>
    public async Task<List<SearchResult>> BrowseCategoryAsync(string categoryKey, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(categoryKey)) return new List<SearchResult>();

        try
        {
            var html = await GetHtmlAsync(BuildCategoryUrl(categoryKey, page), ct);
            return ParseSearchResultsHtml(html);
        }
        catch
        {
            return new List<SearchResult>();
        }
    }

    private static async Task<string> GetHtmlAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UA);

        using var resp = await HttpClientFactory.GetClient().SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return string.Empty;
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static string ParseIso8601Duration(string iso)
    {
        var m = Regex.Match(iso, @"PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?");
        if (!m.Success) return iso;
        var parts = new List<string>();
        if (m.Groups[1].Success) parts.Add(m.Groups[1].Value + ":");
        parts.Add((m.Groups[2].Success ? m.Groups[2].Value.PadLeft(2, '0') : "00") + ":");
        parts.Add(m.Groups[3].Success ? m.Groups[3].Value.PadLeft(2, '0') : "00");
        return string.Concat(parts).TrimEnd(':');
    }

    private static string FormatNumber(string s)
    {
        if (long.TryParse(s, out var n)) return n.ToString("N0");
        return s;
    }

    /// <summary>
    /// Decode \uXXXX Unicode escape sequences in a string (e.g. \u9ebb -> 麻).
    /// PornHub embeds Chinese titles as JSON-style Unicode escapes in page HTML.
    /// </summary>
    private static readonly Regex _unicodeEscape = new Regex(@"\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);
    private static string DecodeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return _unicodeEscape.Replace(s, m =>
            ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    }
}

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
        req.Headers.Add("Cookie", "platform=pc; ageconfirmed=1; age_verified=1; country=CN");
        req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        try
        {
            var resp = await HttpClientFactory.GetClient().SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync(ct);

            // Title
            var m = Regex.Match(html, @"""video_title""\s*:\s*""([^""]+)""");
            if (!m.Success) m = Regex.Match(html, @"<title>([^<]+)");
            if (m.Success) info.Title = DecodeJsonString(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()));

            // Author
            m = Regex.Match(html, @"""author""\s*:\s*\{[^}]*?""name""\s*:\s*""([^""]+)""");
            if (!m.Success) m = Regex.Match(html, @"class=""usernameBadgesWrapper[^""]*""[^>]*>[^<]*<[^>]+>([^<]+)<");
            if (m.Success) info.Author = DecodeJsonString(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()));

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
            var quality = def["quality"]?.ToString() ?? "";
            var videoUrl = def["videoUrl"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(videoUrl)) continue;

            if (format == "hls")
            {
                info.StreamUrls[$"hls_{quality}"] = videoUrl;
                info.Qualities.Add(new QualityOption($"{quality}p (HLS)", $"hls_{quality}"));
            }
            else if (format == "mp4" && videoUrl.Contains("/video/get_media"))
            {
                info.StreamUrls[$"mp4_{quality}"] = videoUrl;
                info.Qualities.Add(new QualityOption($"{quality}p (MP4)", $"mp4_{quality}"));
            }
        }
    }

    public async Task<List<SearchResult>> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        var searchUrl = $"https://www.pornhub.com/video/search?search={Uri.EscapeDataString(query)}&page={page}";
        var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        req.Headers.Add("Cookie", "platform=pc; ageconfirmed=1; age_verified=1; country=CN");
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

    private static List<SearchResult> ParseSearchResultsHtml(string html)
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
            var title = DecodeJsonString(System.Net.WebUtility.HtmlDecode(titleNode.InnerText?.Trim() ?? ""));

            var durNode = item.SelectSingleNode(".//span[contains(@class,'duration')]")
                            ?? item.SelectSingleNode(".//div[contains(@class,'duration')]");
            var duration = durNode?.InnerText?.Trim() ?? "";

            var authorNode = item.SelectSingleNode(".//span[contains(@class,'username')]//a")
                              ?? item.SelectSingleNode(".//a[contains(@class,'username')]");
            var author = System.Net.WebUtility.HtmlDecode(authorNode?.InnerText?.Trim() ?? "");

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

    /// <summary>Fallback category list (well-known PornHub category slugs).</summary>
    private static readonly (string Slug, string Name)[] CuratedCategories =
    [
        ("amateur", "Amateur"), ("anal", "Anal"), ("asian", "Asian"), ("bbw", "BBW"),
        ("big-ass", "Big Ass"), ("big-tits", "Big Tits"), ("blonde", "Blonde"),
        ("blowjob", "Blowjob"), ("brunette", "Brunette"), ("creampie", "Creampie"),
        ("cumshot", "Cumshot"), ("ebony", "Ebony"), ("hentai", "Hentai"),
        ("interracial", "Interracial"), ("japanese", "Japanese"), ("latina", "Latina"),
        ("lesbian", "Lesbian"), ("milf", "MILF"), ("public", "Public"),
        ("redhead", "Redhead"), ("solo-female", "Solo Female"), ("teen", "Teen"),
        ("threesome", "Threesome"), ("toys", "Toys"), ("webcam", "Webcam"),
    ];

    /// <summary>
    /// List PornHub categories. Tries the live /categories page first;
    /// falls back to a built-in list when the markup changed or the page is unreachable.
    /// </summary>
    public async Task<List<CategoryItem>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var results = new List<CategoryItem>();
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://www.pornhub.com/categories");
            req.Headers.Add("Cookie", "platform=pc; ageconfirmed=1; age_verified=1; country=CN");
            req.Headers.Add("User-Agent", UA);

            var resp = await HttpClientFactory.GetClient().SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var html = await resp.Content.ReadAsStringAsync(ct);
                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in doc.DocumentNode.SelectNodes("//a[contains(@href,'/categories/')]") ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
                {
                    var href = a.GetAttributeValue("href", "");
                    var slug = href.Split('?', '#')[0].TrimEnd('/').Split('/').LastOrDefault();
                    if (string.IsNullOrEmpty(slug) || !seen.Add(slug)) continue;

                    var nameNode = a.SelectSingleNode(".//div[contains(@class,'category-title')]");
                    var name = nameNode?.InnerText ?? a.GetAttributeValue("title", "");
                    if (string.IsNullOrWhiteSpace(name)) name = a.InnerText;
                    name = System.Net.WebUtility.HtmlDecode(name ?? "").Trim();
                    // strip trailing video-count text like "12,345 videos"
                    name = Regex.Replace(name, @"[\d,.+\s]*videos?\s*$", "", RegexOptions.IgnoreCase).Trim();
                    if (string.IsNullOrEmpty(name)) name = slug.Replace('-', ' ');

                    var count = 0;
                    var countText = a.SelectSingleNode(".//var")?.InnerText ?? "";
                    var countMatch = Regex.Match(countText, @"[\d,.]+");
                    if (countMatch.Success)
                        int.TryParse(countMatch.Value.Replace(",", "").Replace(".", ""), out count);

                    results.Add(new CategoryItem
                    {
                        Name = name,
                        Key = slug,
                        Site = SiteType.PornHub,
                        VideoCount = count
                    });
                }
            }
        }
        catch
        {
            // Live parse failed — fall back to the curated list below
        }

        if (results.Count == 0)
        {
            results.AddRange(CuratedCategories.Select(c => new CategoryItem
            {
                Name = c.Name,
                Key = c.Slug,
                Site = SiteType.PornHub
            }));
        }
        return results;
    }

    /// <summary>
    /// Browse videos of a PornHub category. Category listing pages use the same
    /// pcVideoListItem markup as search, so the search parser is reused.
    /// </summary>
    public async Task<List<SearchResult>> BrowseCategoryAsync(string categoryKey, int page = 1, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(categoryKey)) return results;

        var url = $"https://www.pornhub.com/video?category={Uri.EscapeDataString(categoryKey)}&page={page}";
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Cookie", "platform=pc; ageconfirmed=1; age_verified=1; country=CN");
            req.Headers.Add("User-Agent", UA);

            var resp = await HttpClientFactory.GetClient().SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return results;
            var html = await resp.Content.ReadAsStringAsync(ct);
            return ParseSearchResultsHtml(html);
        }
        catch
        {
            return results;
        }
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

using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using VideoFetch.Models;

namespace VideoFetch.Services.Sites;

public class XVideosParser : ISiteParser
{
    public SiteType Site => SiteType.XVideos;
    // Get the shared client per call so a proxy change (HttpClientFactory.Configure) takes effect

    public bool CanHandle(string url)
        => !string.IsNullOrWhiteSpace(url) && url.Contains("xvideos.com");

    public async Task<VideoInfo?> FetchVideoInfoAsync(string url, CancellationToken ct = default)
    {
        var info = new VideoInfo { Url = url, Site = Site };
        try
        {
            var resp = await HttpClientFactory.GetClient().GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync(ct);
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);

            // Title
            var titleNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")
                            ?? doc.DocumentNode.SelectSingleNode("//title");
            if (titleNode != null)
            {
                var raw = titleNode.GetAttributeValue("content", "") ?? titleNode.InnerText;
                raw = System.Net.WebUtility.HtmlDecode(raw ?? "");
                raw = Regex.Replace(raw, @"\s*-\s*XVIDEOS?\.COM\s*$", "", RegexOptions.IgnoreCase);
                info.Title = raw.Trim();
            }

            // Author
            var authorNode = doc.DocumentNode.SelectSingleNode("//a[contains(@href,'/model/') or contains(@href,'/channels/')]");
            if (authorNode != null) info.Author = System.Net.WebUtility.HtmlDecode(authorNode.InnerText?.Trim() ?? "");

            // Duration
            var durNode = doc.DocumentNode.SelectSingleNode("//span[contains(@class,'duration')]")
                            ?? doc.DocumentNode.SelectSingleNode("//meta[@property='video:duration']");
            if (durNode != null)
            {
                var dur = durNode.GetAttributeValue("content", "") ?? durNode.InnerText;
                info.Duration = dur.Trim();
            }

            // Thumbnail
            var thumbNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
            if (thumbNode != null) info.ThumbnailUrl = thumbNode.GetAttributeValue("content", "");

            // Stream URLs from flashvars
            var flashvarsMatch = Regex.Match(html, @"var\s+flashvars_\w+\s*=\s*(\{.*?\});", RegexOptions.Singleline);
            if (flashvarsMatch.Success)
            {
                try
                {
                    var json = Newtonsoft.Json.Linq.JObject.Parse(flashvarsMatch.Groups[1].Value);
                    var mediaDefs = json["mediaDefinitions"] as Newtonsoft.Json.Linq.JArray;
                    if (mediaDefs != null)
                    {
                        foreach (Newtonsoft.Json.Linq.JObject def in mediaDefs)
                        {
                            var format = def["format"]?.ToString() ?? "";
                            var quality = def["quality"]?.ToString() ?? "unknown";
                            var videoUrl = def["videoUrl"]?.ToString() ?? "";
                            if (string.IsNullOrEmpty(videoUrl)) continue;
                            info.StreamUrls[$"{format}_{quality}"] = videoUrl;
                            info.Qualities.Add(new QualityOption($"{quality}p ({format.ToUpper()})", $"{format}_{quality}"));
                        }
                    }
                }
                catch { }
            }

            // Fallback: look for setVideoHLS/setVideoUrlHigh in HTML
            ExtractStreams(html, info);
            return info;
        }
        catch { return null; }
    }

    private static void ExtractStreams(string html, VideoInfo info)
    {
        var hlsMatch = Regex.Match(html, @"setVideoHLS\('([^']+)'");
        if (hlsMatch.Success)
        {
            info.StreamUrls["hls"] = hlsMatch.Groups[1].Value;
            info.Qualities.Add(new QualityOption("Best (HLS)", "hls"));
        }

        var highMatch = Regex.Match(html, @"setVideoUrlHigh\('([^']+)'");
        if (highMatch.Success)
        {
            info.StreamUrls["high"] = highMatch.Groups[1].Value;
            info.Qualities.Add(new QualityOption("High", "high"));
        }

        var lowMatch = Regex.Match(html, @"setVideoUrlLow\('([^']+)'");
        if (lowMatch.Success)
        {
            info.StreamUrls["low"] = lowMatch.Groups[1].Value;
            info.Qualities.Add(new QualityOption("Low", "low"));
        }

        // XVideos also uses __model as JSON
        var modelMatch = Regex.Match(html, @"__model\s*=\s*(\{.*?\});", RegexOptions.Singleline);
        if (modelMatch.Success)
        {
            try
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(modelMatch.Groups[1].Value);
                var videoUrl = json["clipUrl"]?.ToString() ?? json["url"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(videoUrl))
                {
                    info.StreamUrls["direct"] = videoUrl;
                    if (!info.Qualities.Any(q => q.Value == "direct"))
                        info.Qualities.Add(new QualityOption("Direct", "direct"));
                }
            }
            catch { }
        }
    }

    // ═══════════════════════════════════════════════════════
    //  SEARCH
    // ═══════════════════════════════════════════════════════

    public async Task<List<SearchResult>> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        // XVideos search URL format
        var searchUrl = $"https://www.xvideos.com/?k={Uri.EscapeDataString(query)}&p={page}";
        try
        {
            var resp = await HttpClientFactory.GetClient().GetAsync(searchUrl, ct);
            if (!resp.IsSuccessStatusCode) return results;
            var html = await resp.Content.ReadAsStringAsync(ct);
            return ParseSearchHtml(html);
        }
        catch { return results; }
    }

    // ═══════════════════════════════════════════════════════
    //  CATEGORIES
    // ═══════════════════════════════════════════════════════

    /// <summary>Fallback category keywords (browsed via keyword search).</summary>
    private static readonly (string Key, string Name)[] CuratedCategories =
    [
        ("amateur", "Amateur"), ("anal", "Anal"), ("asian", "Asian"), ("bbw", "BBW"),
        ("big ass", "Big Ass"), ("big tits", "Big Tits"), ("blonde", "Blonde"),
        ("brunette", "Brunette"), ("creampie", "Creampie"), ("cumshot", "Cumshot"),
        ("ebony", "Ebony"), ("hentai", "Hentai"), ("interracial", "Interracial"),
        ("japanese", "Japanese"), ("latina", "Latina"), ("lesbian", "Lesbian"),
        ("milf", "MILF"), ("public", "Public"), ("redhead", "Redhead"),
        ("teen", "Teen"), ("threesome", "Threesome"), ("webcam", "Webcam"),
    ];

    /// <summary>
    /// List XVideos categories. Tries the live /categories page (links to /c/...)
    /// first; falls back to a built-in keyword list.
    /// </summary>
    public async Task<List<CategoryItem>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var results = new List<CategoryItem>();
        try
        {
            var resp = await HttpClientFactory.GetClient().GetAsync("https://www.xvideos.com/categories", ct);
            if (resp.IsSuccessStatusCode)
            {
                var html = await resp.Content.ReadAsStringAsync(ct);
                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
                {
                    var href = a.GetAttributeValue("href", "");
                    if (string.IsNullOrEmpty(href) || !href.Contains("/c/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var path = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? Uri.TryCreate(href, UriKind.Absolute, out var u) ? u.AbsolutePath : ""
                        : href.Split('?')[0];
                    path = path.TrimEnd('/');
                    if (!path.StartsWith("/c/") || path.Length <= 3) continue;
                    var key = path[3..];
                    if (!seen.Add(key)) continue;

                    var name = System.Net.WebUtility.HtmlDecode(a.InnerText ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        name = key.Split('-')[0].Replace('_', ' ');

                    results.Add(new CategoryItem
                    {
                        Name = name,
                        Key = path,          // "/c/xxx" → path-based browse
                        Site = SiteType.XVideos
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
                Key = c.Key,             // plain keyword → browsed via search
                Site = SiteType.XVideos
            }));
        }
        return results;
    }

    /// <summary>
    /// Browse an XVideos category. Keys are either a "/c/xxx" path (from the live
    /// categories page) or a plain keyword (curated fallback → keyword search).
    /// </summary>
    public async Task<List<SearchResult>> BrowseCategoryAsync(string categoryKey, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(categoryKey)) return new List<SearchResult>();

        // Keyword key → straight to search
        if (!categoryKey.StartsWith("/"))
            return await SearchAsync(categoryKey, page, ct);

        // Path-based category page; pagination appends /{page}
        var url = "https://www.xvideos.com" + categoryKey;
        if (page > 1) url += $"/{page}";
        try
        {
            var resp = await HttpClientFactory.GetClient().GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                var html = await resp.Content.ReadAsStringAsync(ct);
                var parsed = ParseSearchHtml(html);
                if (parsed.Count > 0) return parsed;
            }
        }
        catch
        {
            // fall through to keyword search below
        }

        // Category page failed → search with the category slug
        var slug = categoryKey.Split('/').LastOrDefault() ?? "";
        return await SearchAsync(slug.Split('-')[0].Replace('_', ' '), page, ct);
    }

    // ═══════════════════════════════════════════════════════
    //  INTERNAL HELPERS
    // ═══════════════════════════════════════════════════════

    private static List<SearchResult> ParseSearchHtml(string html)
    {
        var results = new List<SearchResult>();
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        // XVideos uses <div class="thumb-block"> for each video
        var items = doc.DocumentNode.SelectNodes("//div[contains(@class,'thumb-block')]")
                    ?? doc.DocumentNode.SelectNodes("//div[contains(@class,'frame-block')]");

        if (items == null) return results;

        foreach (var item in items)
        {
            // The link with title is the <a> inside the thumb
            var linkNode = item.SelectSingleNode(".//a[contains(@class,'thumb-info')]")
                            ?? item.SelectSingleNode(".//a[contains(@href,'/video')]");
            if (linkNode == null) continue;

            var href = linkNode.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(href)) continue;
            var url = href.StartsWith("http") ? href : "https://www.xvideos.com" + href;

            // Title: try attribute first, then nested span/p text
            var title = linkNode.GetAttributeValue("title", "");
            if (string.IsNullOrWhiteSpace(title))
            {
                // Look for the title text inside the link (usually in a <p> or <span>)
                var titleNode = item.SelectSingleNode(".//p[contains(@class,'title')]")
                                ?? item.SelectSingleNode(".//span[contains(@class,'title')]")
                                ?? item.SelectSingleNode(".//div[contains(@class,'thumb-under')]//a")
                                ?? linkNode;
                title = titleNode.InnerText?.Trim() ?? "";
            }
            title = System.Net.WebUtility.HtmlDecode(title);
            title = Regex.Replace(title, "<[^>]*>", "").Trim();

            // Duration
            var durNode = item.SelectSingleNode(".//span[contains(@class,'duration')]")
                            ?? item.SelectSingleNode(".//div[contains(@class,'duration')]");
            var duration = durNode?.InnerText?.Trim() ?? "";

            // Author
            var authorNode = item.SelectSingleNode(".//a[contains(@href,'/model/') or contains(@href,'/channels/')]")
                            ?? item.SelectSingleNode(".//span[contains(@class,'author')]");
            var author = System.Net.WebUtility.HtmlDecode(authorNode?.InnerText?.Trim() ?? "");

            // Thumbnail
            var imgNode = item.SelectSingleNode(".//img");
            var thumb = ParserHelper.GetThumbUrl(imgNode);

            results.Add(new SearchResult
            {
                Title = title,
                Url = url,
                Author = author,
                Duration = duration,
                ThumbnailUrl = thumb,
                Site = SiteType.XVideos
            });
        }
        return results;
    }
}

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.IO;
using VideoFetch.Models;

namespace VideoFetch.Services.Sites;

/// <summary>
/// Parser for 91Pinse.com — extracts video streams and search results.
///
/// Based on DeepGrab reference implementation.
/// The site loads video via iframe (fplayer.cc or similar) containing
/// m3u8/mp4 URLs, sometimes Base64-encoded.
/// Fallback: direct regex extraction from main page HTML.
/// </summary>
public class PinSeParser : ISiteParser
{
    // ── Constants ───────────────────────────────────────────

    private const string BaseUrl = "https://91pinse.com";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36";
    private const string AcceptHdr =
        "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const string LangHdr = "zh-CN,zh;q=0.9,en;q=0.8";

    private static readonly HttpClient _client = HttpClientFactory.GetClient();

    // ── ISiteParser ─────────────────────────────────────────

    public SiteType Site => SiteType.PinSe;

    public bool CanHandle(string url)
        => !string.IsNullOrWhiteSpace(url) && url.Contains("91pinse.com");

    // ═══════════════════════════════════════════════════════
    //  FETCH VIDEO INFO
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Fetch video metadata and stream URLs from a 91Pinse video page.
    ///
    /// Strategy (multi-level fallback):
    /// 1. Fetch main page HTML, extract title + iframe URLs
    /// 2. Dive into iframe pages, extract m3u8/mp4 URLs (including Base64-encoded)
    /// 3. Fallback: extract m3u8/mp4 URLs directly from main page HTML
    /// </summary>
    public async Task<VideoInfo?> FetchVideoInfoAsync(string url, CancellationToken ct = default)
    {
        var info = new VideoInfo { Url = url, Site = SiteType.PinSe };

        try
        {
            // Step 1 — Fetch main page
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", AcceptHdr);
            req.Headers.TryAddWithoutValidation("Accept-Language", LangHdr);

            using var resp = await _client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            var html = await resp.Content.ReadAsStringAsync(ct);

            // Title
            info.Title = ExtractPageTitle(html);

            // Step 2 — Try main page for direct media URLs
            var mainUrls = ExtractMediaUrls(html, finalUrl);
            mainUrls.AddRange(ExtractBase64MediaUrls(html, finalUrl));
            var best = PickBestMediaUrl(mainUrls);
            if (best != null)
            {
                PopulateStreams(info, best, "main-page");
                return info;
            }

            // Step 3 — Dive into iframes
            var iframeUrls = ExtractIframeUrls(html, finalUrl);
            if (iframeUrls.Count == 0)
            {
                // No iframes and no direct URLs — can't extract
                return info;
            }

            foreach (var iframeUrl in iframeUrls)
            {
                try
                {
                    using var r2 = new HttpRequestMessage(HttpMethod.Get, iframeUrl);
                    r2.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                    r2.Headers.TryAddWithoutValidation("Accept", AcceptHdr);
                    r2.Headers.TryAddWithoutValidation("Accept-Language", LangHdr);
                    r2.Headers.Referrer = new Uri(finalUrl);

                    using var resp2 = await _client.SendAsync(r2, ct);
                    resp2.EnsureSuccessStatusCode();
                    var iframeHtml = await resp2.Content.ReadAsStringAsync(ct);

                    var urls = ExtractMediaUrls(iframeHtml, iframeUrl);
                    urls.AddRange(ExtractBase64MediaUrls(iframeHtml, iframeUrl));
                    best = PickBestMediaUrl(urls);
                    if (best != null)
                    {
                        PopulateStreams(info, best, "iframe");
                        return info;
                    }
                }
                catch
                {
                    // Try next iframe
                }
            }

            // No usable streams found but info is still useful for display
            return info;
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  SEARCH
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Search 91Pinse by keyword.
    /// URL: https://91pinse.com/v/search?keyword=xxx
    /// </summary>
    public async Task<List<SearchResult>> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        var searchUrl = BuildSearchUrl(query, page);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", AcceptHdr);
            req.Headers.TryAddWithoutValidation("Accept-Language", LangHdr);

            using var resp = await _client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return results;
            var html = await resp.Content.ReadAsStringAsync(ct);

            return ParseSearchHtml(html);
        }
        catch
        {
            return results;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  PRIVATE — Page Title
    // ═══════════════════════════════════════════════════════

    private static string ExtractPageTitle(string html)
    {
        var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
        {
            var t = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            if (!string.IsNullOrWhiteSpace(t))
                return SanitizeFileName(t);
        }
        return $"pinse_{DateTime.Now:HHmmss}";
    }

    // ═══════════════════════════════════════════════════════
    //  PRIVATE — Iframe URL extraction
    // ═══════════════════════════════════════════════════════

    private static List<string> ExtractIframeUrls(string html, string baseUrl)
    {
        var urls = new List<string>();

        // Standard <iframe src="...">
        foreach (Match m in Regex.Matches(html,
            @"<iframe[^>]+src=[""']?([^""'>\s]+)", RegexOptions.IgnoreCase))
        {
            var s = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(s))
                urls.Add(UrlJoin(baseUrl, s));
        }

        // fplayer.cc direct src references
        var m1 = Regex.Match(html,
            @"src=[""'](https?://fplayer\.cc/embed/[^""']+)", RegexOptions.IgnoreCase);
        if (m1.Success) urls.Insert(0, m1.Groups[1].Value);

        var m2 = Regex.Match(html,
            @"src=(https?://fplayer\.cc/embed/[^\s>]+)", RegexOptions.IgnoreCase);
        if (m2.Success) urls.Insert(0, m2.Groups[1].Value);

        return urls.Distinct().ToList();
    }

    // ═══════════════════════════════════════════════════════
    //  PRIVATE — Media URL extraction (m3u8 / mp4)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Extract .m3u8 and .mp4 URLs from raw HTML/JS text using regex patterns.
    /// Handles escaped slashes (\/) and unicode escapes (\u002F).
    /// </summary>
    private static List<string> ExtractMediaUrls(string text, string baseUrl)
    {
        var urls = new List<string>();

        // Pattern groups: (regex, capture_group)
        (string Pattern, int Group)[] patterns =
        [
            (@"(https?:\\?/\\?/[^""'\s]+?\.(?:m3u8|mp4)(?:\?[^""'\s]*)?)", 1),
            (@"(//[^""'\s]+?\.(?:m3u8|mp4)(?:\?[^""'\s]*)?)", 1),
            (@"([""'])([^""']+?\.(?:m3u8|mp4)(?:\?[^""']*)?)\1", 2),
        ];

        foreach (var (pattern, group) in patterns)
        {
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
            {
                var raw = m.Groups[group].Value;
                if (string.IsNullOrEmpty(raw)) continue;

                // Unescape common JS encodings
                var url = raw
                    .Replace("\\u002F", "/")
                    .Replace("\\/", "/")
                    .Replace("\\u0026", "&")
                    .Trim();

                if (url.StartsWith("//"))
                    url = UrlJoin(baseUrl, url);
                else if (url.StartsWith("/"))
                    url = UrlJoin(baseUrl, url);

                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    urls.Add(url);
                }
            }
        }

        return urls.Distinct().ToList();
    }

    /// <summary>
    /// Some 91Pinse pages encode media URLs in Base64 strings within JS/HTML.
    /// Try to detect and decode them.
    /// </summary>
    private static List<string> ExtractBase64MediaUrls(string text, string baseUrl)
    {
        var urls = new List<string>();

        // Look for long Base64-like strings in quotes
        foreach (Match m in Regex.Matches(text,
            @"[""']([A-Za-z0-9+/\\=]{24,})[""']"))
        {
            try
            {
                var raw = m.Groups[1].Value
                    .Replace("\\u003D", "=")
                    .Replace("\\/", "/");
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                urls.AddRange(ExtractMediaUrls(decoded, baseUrl));
            }
            catch
            {
                // Not valid Base64 — skip
            }
        }

        return urls.Distinct().ToList();
    }

    /// <summary>
    /// Choose the best quality URL from extracted candidates.
    /// Prefers master.m3u8 > regular m3u8 > mp4, penalizes trailer/preview links.
    /// </summary>
    private static string? PickBestMediaUrl(List<string> urls)
    {
        if (urls.Count == 0) return null;

        // Filter out template placeholders and known garbage
        var clean = urls
            .Where(u =>
                !u.Contains('{') &&
                !u.Contains('}') &&
                !u.ToLower().Contains("ping.m3u8") &&
                !u.ToLower().StartsWith("blob:"))
            .ToList();

        if (clean.Count == 0) clean = urls;

        // Score each URL — higher is better
        return clean.MaxBy(u =>
        {
            var l = u.ToLower();
            var score = 0;
            if (l.Contains(".m3u8")) score += 300;
            else if (l.Contains(".mp4")) score += 200;
            if (l.Contains("master.m3u8")) score += 120;
            if (l.Contains("expires=")) score += 60;
            if (l.Contains("trailer")) score -= 400;
            if (l.Contains("preview") || l.Contains("thumb")) score -= 150;
            return score;
        });
    }

    /// <summary>
    /// Add the stream URL to VideoInfo with quality/key label.
    /// </summary>
    private static void PopulateStreams(VideoInfo info, string url, string source)
    {
        var key = url.Contains(".m3u8") ? $"hls_{source}" : $"mp4_{source}";
        info.StreamUrls[key] = url;
        info.Qualities.Add(new QualityOption(
            url.Contains(".m3u8") ? $"HLS ({source})" : $"MP4 ({source})",
            key));
    }

    // ═══════════════════════════════════════════════════════
    //  PRIVATE — Search HTML parsing
    // ═══════════════════════════════════════════════════════

    private static string BuildSearchUrl(string keyword, int page)
    {
        var encoded = Uri.EscapeDataString(keyword);
        var url = $"https://91pinse.com/v/search?keyword={encoded}";
        if (page > 1) url += $"&page={page}";
        return url;
    }

    /// <summary>
    /// Parse search/browse HTML into SearchResult list.
    ///
    /// Current site structure (Tailwind CSS):
    /// <code>
    ///   &lt;article class="video-card group/card"&gt;
    ///     &lt;a href="/v/440277" aria-label="English desc"&gt;
    ///       &lt;div class="video-thumb"&gt;
    ///         &lt;img src="thumb.webp" alt="English desc"&gt;
    ///         &lt;span class="video-duration"&gt;0:46:05&lt;/span&gt;
    ///       &lt;/div&gt;
    ///     &lt;/a&gt;
    ///     &lt;div class="video-card-body"&gt;
    ///       &lt;a class="video-card-title" title="真实中文标题"&gt;真实中文标题&lt;/a&gt;
    ///       &lt;a class="video-card-author" title="AuthorName"&gt;AuthorName&lt;/a&gt;
    ///     &lt;/div&gt;
    ///   &lt;/article&gt;
    /// </code>
    /// </summary>
    private List<SearchResult> ParseSearchHtml(string html)
    {
        var results = new List<SearchResult>();
        var seen = new HashSet<string>();

        // Extract each <article class="video-card group/card"> ... </article> block.
        // Use non-greedy match to capture one card at a time.
        var cardMatches = Regex.Matches(html,
            @"<article\s+class=""video-card\s+group/card"">(.*?)</article>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match cardMatch in cardMatches)
        {
            var card = cardMatch.Groups[1].Value;

            // ── Video URL: /v/440277 ──
            var hrefMatch = Regex.Match(card,
                @"<a[^>]*href=""(/v/\d+)[^""]*""",
                RegexOptions.IgnoreCase);
            if (!hrefMatch.Success) continue;
            var href = hrefMatch.Groups[1].Value.Trim();
            if (!seen.Add(href)) continue;

            // ── Title: prefer video-card-title (matches download filename when available),
            //     fallback to aria-label. Note: search page titles are English,
            //     detail page titles are Chinese — this is a site design difference. ──
            var title = "";
            var titleMatch = Regex.Match(card,
                @"<a[^>]*class=""video-card-title""[^>]*title=""([^""]+)""",
                RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                title = WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim();
                title = Regex.Replace(title, @"<[^>]*>", "");  // strip <mark> etc
            }
            else
            {
                var ariaMatch = Regex.Match(card,
                    @"aria-label=""([^""]+)""",
                    RegexOptions.IgnoreCase);
                if (ariaMatch.Success)
                {
                    title = WebUtility.HtmlDecode(ariaMatch.Groups[1].Value).Trim();
                    title = Regex.Replace(title, @"<[^>]*>", "");
                }
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                // Last resort: inner text of video-card-title
                var innerMatch = Regex.Match(card,
                    @"<a[^>]*class=""video-card-title""[^>]*>(.*?)</a>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (innerMatch.Success)
                    title = WebUtility.HtmlDecode(Regex.Replace(innerMatch.Groups[1].Value, @"<[^>]*>", "")).Trim();
            }
            if (string.IsNullOrWhiteSpace(title))
                title = href;

            // ── Duration: <span class="video-duration">0:46:05</span> ──
            var dur = "";
            var durMatch = Regex.Match(card,
                @"<span[^>]*class=""[^""]*video-duration[^""]*""[^>]*>\s*([\d:]+)\s*</span>",
                RegexOptions.IgnoreCase);
            if (durMatch.Success) dur = durMatch.Groups[1].Value.Trim();

            // ── Thumbnail: <img src="..."> ──
            var thumb = "";
            var imgMatch = Regex.Match(card,
                @"<img[^>]*src=""([^""]+)""",
                RegexOptions.IgnoreCase);
            if (imgMatch.Success)
            {
                thumb = imgMatch.Groups[1].Value;
                // Handle protocol-relative URLs
                if (thumb.StartsWith("//"))
                    thumb = "https:" + thumb;
            }

            // ── Author: <a class="video-card-author" title="Name"> ──
            var author = "";
            var authorMatch = Regex.Match(card,
                @"<a[^>]*class=""[^""]*video-card-author[^""]*""[^>]*title=""([^""]+)""",
                RegexOptions.IgnoreCase);
            if (authorMatch.Success)
                author = WebUtility.HtmlDecode(authorMatch.Groups[1].Value).Trim();

            results.Add(new SearchResult
            {
                Title = title,
                Url = $"{BaseUrl}{href}",
                Duration = dur,
                Author = author,
                ThumbnailUrl = thumb,
                Site = SiteType.PinSe,
            });
        }

        return results;
    }

    // ═══════════════════════════════════════════════════════
    //  PRIVATE — Utilities
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Join a relative URL with a base URL.
    /// </summary>
    private static string UrlJoin(string baseUrl, string relative)
    {
        try { return new Uri(new Uri(baseUrl), relative).ToString(); }
        catch { return relative; }
    }

    /// <summary>
    /// Remove characters illegal in file names, trim to 120 chars max.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var inv = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(inv.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().Trim('.', ' ');
        if (s.Length > 120) s = s[..120];
        return string.IsNullOrWhiteSpace(s)
            ? $"pinse_{DateTime.Now:HHmmss}"
            : s;
    }
}

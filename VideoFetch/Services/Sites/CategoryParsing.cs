using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Newtonsoft.Json.Linq;
using VideoFetch.Models;

namespace VideoFetch.Services.Sites;

/// <summary>
/// Pure (no network) category extraction for every supported site.
///
/// Why this file exists: the category lists used to be parsed with selectors
/// that no longer matched the live markup (or pointed at pages that now 404),
/// so every site silently fell back to a hand-written list of made-up names —
/// "分类加载的都是不对的". Each parser below is pinned to the real markup that
/// the sites served in Oct 2026; the HTML fixtures in VideoFetch.Test/Fixtures
/// are excerpts of those exact pages.
///
/// Markup contract per site:
///   XVideos : nav sub-list  &lt;li class="dyntop-cat top-cat-N"&gt;&lt;a href="/c/Slug-ID"&gt;Name&lt;/a&gt;&lt;/li&gt;
///   PornHub : grid           &lt;li class="catPic" data-category="28"&gt; … &lt;strong&gt;Mature&lt;/strong&gt; &lt;var&gt;48,862&lt;/var&gt; Videos
///   XNXX    : embedded json  "categories":[{"label":"Toons","url":"/search/toons",…}, …]
///   PinSe   : 热门搜索 pill cloud + homepage "home-chip" links (keyword based)
/// </summary>
public static class CategoryParsing
{
    private const int MaxNameLength = 60;

    // ═══════════════════════════════════════════════════════
    //  XVideos  —  https://www.xvideos.com/tags (nav sub-list)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// XVideos renders its real category menu inside the header sub-list.
    /// Keys are path-form ("/c/Amateur-65") because the numeric id is required
    /// by the site — "/c/amateur" alone now 404s.
    /// </summary>
    public static List<CategoryItem> ParseXVideos(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return new List<CategoryItem>();

        var items = new List<CategoryItem>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var links = doc.DocumentNode.SelectNodes("//li[contains(@class,'dyntop-cat')]//a[@href]")
                    ?? doc.DocumentNode.SelectNodes("//a[starts-with(@href,'/c/')]");
        if (links == null) return items;

        foreach (var a in links)
        {
            var href = a.GetAttributeValue("href", "").Trim();
            var m = Regex.Match(href, @"^/c/([^/?#]+-\d+)/?$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;   // skip /c/ links without the id, /categories/*, /tags/*

            var name = FirstNonEmpty(
                a.GetAttributeValue("title", ""),
                a.InnerText);

            items.Add(new CategoryItem
            {
                Name = name,
                Key = "/c/" + m.Groups[1].Value,
                Site = SiteType.XVideos
            });
        }

        return Normalize(items, SiteType.XVideos);
    }

    /// <summary>Category page URL. Pagination on XVideos appends "/{page}".</summary>
    public static string BuildXVideosUrl(string key, int page)
    {
        key = (key ?? "").Trim();
        if (!key.StartsWith("/c/"))
            key = "/c/" + key.Trim('/');
        var url = "https://www.xvideos.com" + key.TrimEnd('/');
        return page > 1 ? $"{url}/{page}" : url;
    }

    // ═══════════════════════════════════════════════════════
    //  PornHub  —  https://www.pornhub.com/categories
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// PornHub ships ~115 categories in a &lt;li class="catPic" data-category="N"&gt;
    /// grid. The numeric id is the browse key (/video?c=N) — the old
    /// "?category=slug" URL is gone.
    /// </summary>
    public static List<CategoryItem> ParsePornHub(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return new List<CategoryItem>();

        var items = new List<CategoryItem>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var nodes = doc.DocumentNode.SelectNodes("//li[contains(@class,'catPic')]");
        if (nodes == null)
        {
            // Older / fallback markup: plain links to /categories/{slug}
            foreach (var a in doc.DocumentNode.SelectNodes("//a[starts-with(@href,'/categories/')]")
                             ?? Enumerable.Empty<HtmlNode>())
            {
                var href = a.GetAttributeValue("href", "");
                var slug = href.Split('?', '#')[0].TrimEnd('/').Split('/').LastOrDefault();
                if (string.IsNullOrWhiteSpace(slug)) continue;
                items.Add(new CategoryItem
                {
                    Name = FirstNonEmpty(a.GetAttributeValue("title", ""), a.InnerText),
                    Key = slug,
                    Site = SiteType.PornHub
                });
            }
            return Normalize(items, SiteType.PornHub);
        }

        foreach (var li in nodes)
        {
            var id = li.GetAttributeValue("data-category", "").Trim();
            var link = li.SelectSingleNode(".//a[@href]");
            var href = link?.GetAttributeValue("href", "") ?? "";

            // Prefer the numeric id; fall back to ?c=N or /categories/{slug}
            string key = id;
            if (!IsDigits(key))
            {
                var m = Regex.Match(href, @"[?&]c=(\d+)");
                key = m.Success ? m.Groups[1].Value : "";
            }
            if (!IsDigits(key))
            {
                var m = Regex.Match(href, @"/categories/([^/?#]+)");
                if (m.Success) key = m.Groups[1].Value;
            }
            if (string.IsNullOrWhiteSpace(key)) continue;

            var name = FirstNonEmpty(
                li.SelectSingleNode(".//span[contains(@class,'categoryTitleWrapper')]//strong")?.InnerText,
                li.SelectSingleNode(".//strong")?.InnerText,
                li.SelectSingleNode(".//img")?.GetAttributeValue("alt", ""),
                link?.GetAttributeValue("alt", ""),
                link?.InnerText);

            // Strip the "* Porn Category" suffix PornHub puts in img alt text
            name = Regex.Replace(name, @"\s+porn\s+category$", "", RegexOptions.IgnoreCase);

            items.Add(new CategoryItem
            {
                Name = name,
                Key = key,
                Site = SiteType.PornHub,
                VideoCount = ParseCount(li.SelectSingleNode(".//var")?.InnerText
                                        ?? li.SelectSingleNode(".//span[contains(@class,'videoCount')]")?.InnerText)
            });
        }

        return Normalize(items, SiteType.PornHub);
    }

    /// <summary>Category page URL. PornHub paginates with &amp;page=N.</summary>
    public static string BuildPornHubUrl(string key, int page)
    {
        key = (key ?? "").Trim();
        var url = IsDigits(key)
            ? $"https://www.pornhub.com/video?c={key}"
            : $"https://www.pornhub.com/categories/{key.Trim('/')}";
        return page > 1
            ? url + (url.Contains('?') ? "&" : "?") + "page=" + page
            : url;
    }

    // ═══════════════════════════════════════════════════════
    //  XNXX  —  embedded "categories" JSON
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// XNXX no longer renders a category list: /tags is only an A–Z index
    /// (which is why the UI used to show categories named "A", "B", "C"…).
    /// The real ~158 categories ship inside the page config as
    /// <c>"categories":[{"label":…,"url":"/search/…"}]</c>.
    /// </summary>
    public static List<CategoryItem> ParseXNxx(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return new List<CategoryItem>();

        List<CategoryItem> best = new();

        // A page can hold several "categories" arrays; keep the biggest one.
        foreach (var arrayJson in ExtractJsonArrays(html, "\"categories\""))
        {
            var parsed = TryParseXnxxArray(arrayJson);
            if (parsed.Count > best.Count) best = parsed;
        }

        return Normalize(best, SiteType.XNxx);
    }

    private static List<CategoryItem> TryParseXnxxArray(string arrayJson)
    {
        var items = new List<CategoryItem>();
        JArray arr;
        try { arr = JArray.Parse(arrayJson); }
        catch { return items; }

        foreach (var token in arr)
        {
            if (token is not JObject obj) continue;

            var label = obj["label"]?.ToString() ?? "";
            var url = obj["url"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(url)) continue;

            // URLs come JSON-escaped ("\/search\/toons") — JObject already unescaped them
            var key = url.Trim();
            if (key.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try { key = new Uri(key).PathAndQuery; } catch { }
            }
            if (!key.StartsWith("/")) key = "/" + key;

            var count = 0;
            int.TryParse(obj["nbvids"]?.ToString(), out count);

            items.Add(new CategoryItem
            {
                Name = label,
                Key = key,
                Site = SiteType.XNxx,
                VideoCount = count
            });
        }

        return items;
    }

    /// <summary>Category page URL. XNXX paginates with "/{page}".</summary>
    public static string BuildXNxxUrl(string key, int page)
    {
        key = (key ?? "").Trim();
        var url = "https://www.xnxx.com" + (key.StartsWith("/") ? key : "/" + key);
        url = url.TrimEnd('/');
        return page > 1 ? $"{url}/{page}" : url;
    }

    // ═══════════════════════════════════════════════════════
    //  PinSe (91pinse)  —  热门搜索 tag cloud / homepage chips
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// 91pinse has no category taxonomy; it exposes a "热门搜索" (popular
    /// searches) pill cloud on listing pages and a "热门" chip row on the
    /// homepage. Both are keyword based, so the browse key is the keyword.
    /// </summary>
    public static List<CategoryItem> ParsePinSe(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return new List<CategoryItem>();

        var items = new List<CategoryItem>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // 1) 热门搜索 section of /v/ and search pages
        foreach (var heading in doc.DocumentNode.SelectNodes("//h3[contains(.,'热门搜索')]")
                                 ?? Enumerable.Empty<HtmlNode>())
        {
            var container = heading.ParentNode ?? heading;
            foreach (var a in container.SelectNodes(".//a[contains(@href,'/v/search?keyword=')]")
                             ?? Enumerable.Empty<HtmlNode>())
                AddPinSeLink(items, a);
        }

        // 2) homepage 热门 chips
        if (items.Count == 0)
        {
            foreach (var a in doc.DocumentNode.SelectNodes("//a[contains(@class,'home-chip')]")
                             ?? Enumerable.Empty<HtmlNode>())
                AddPinSeLink(items, a);
        }

        // 3) last resort — any keyword link on the page
        if (items.Count == 0)
        {
            foreach (var a in doc.DocumentNode.SelectNodes("//a[contains(@href,'/v/search?keyword=')]")
                             ?? Enumerable.Empty<HtmlNode>())
                AddPinSeLink(items, a);
        }

        return Normalize(items, SiteType.PinSe);
    }

    private static void AddPinSeLink(List<CategoryItem> items, HtmlNode a)
    {
        var href = a.GetAttributeValue("href", "");
        var keyword = ExtractQueryValue(href, "keyword");
        if (string.IsNullOrWhiteSpace(keyword)) return;

        var name = FirstNonEmpty(a.GetAttributeValue("title", ""), a.InnerText);
        items.Add(new CategoryItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? keyword : name,
            Key = keyword,
            Site = SiteType.PinSe
        });
    }

    /// <summary>Category page URL — a keyword search, paginated with &amp;page=N.</summary>
    public static string BuildPinSeUrl(string key, int page)
    {
        var keyword = (key ?? "").Trim();
        var url = "https://91pinse.com/v/search?keyword=" + Uri.EscapeDataString(keyword);
        return page > 1 ? url + "&page=" + page : url;
    }

    // ═══════════════════════════════════════════════════════
    //  shared helpers
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Clean, de-duplicate and sanity-check the parsed rows. Anything that
    /// cannot be shown or browsed (empty name/key, placeholder templates,
    /// single-letter A–Z index entries) is dropped here.
    /// </summary>
    internal static List<CategoryItem> Normalize(IEnumerable<CategoryItem> items, SiteType site)
    {
        var result = new List<CategoryItem>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (item == null) continue;

            var name = CleanText(item.Name);
            var key = (item.Key ?? "").Trim();

            if (name.Length == 0 || key.Length == 0) continue;
            if (name.Length > MaxNameLength) continue;
            if (key.Contains('{') || key.Contains('}')) continue;   // "{search_term_string}"
            if (name.Contains('{') || name.Contains('}')) continue;
            if (name.Length == 1) continue;                          // A–Z index leftovers

            if (!seenKeys.Add(key)) continue;
            if (!seenNames.Add(name)) continue;

            item.Name = name;
            item.Key = key;
            item.Site = site;
            result.Add(item);
        }

        return result;
    }

    /// <summary>Decode entities, drop inline tags, collapse whitespace.</summary>
    internal static string CleanText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var text = WebUtility.HtmlDecode(raw);
        text = Regex.Replace(text, @"<[^>]*>", " ");
        text = text.Replace('\u00a0', ' ');
        text = Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            var cleaned = CleanText(c);
            if (cleaned.Length > 0) return cleaned;
        }
        return string.Empty;
    }

    private static bool IsDigits(string? s)
        => !string.IsNullOrEmpty(s) && s.All(char.IsDigit);

    private static int ParseCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var digits = Regex.Match(text, @"[\d,.]+").Value.Replace(",", "").Replace(".", "");
        return int.TryParse(digits, out var n) ? n : 0;
    }

    /// <summary>Read a query-string value from a (possibly partial) URL.</summary>
    private static string ExtractQueryValue(string url, string name)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var m = Regex.Match(url, @"[?&]" + Regex.Escape(name) + @"=([^&#""']*)", RegexOptions.IgnoreCase);
        if (!m.Success) return string.Empty;

        var raw = m.Groups[1].Value.Replace("+", " ");
        try { return Uri.UnescapeDataString(raw).Trim(); }
        catch { return raw.Trim(); }
    }

    /// <summary>
    /// Find every JSON array that follows <paramref name="propertyName"/> in the
    /// raw HTML/JS and return its text. Uses a quote-aware bracket scan, so
    /// arrays of objects with nested arrays parse correctly.
    /// </summary>
    private static IEnumerable<string> ExtractJsonArrays(string html, string propertyName)
    {
        var searchFrom = 0;
        while (true)
        {
            var idx = html.IndexOf(propertyName, searchFrom, StringComparison.Ordinal);
            if (idx < 0) yield break;
            searchFrom = idx + propertyName.Length;

            var open = html.IndexOf('[', searchFrom);
            if (open < 0) yield break;

            var end = FindArrayEnd(html, open);
            if (end < 0) continue;

            yield return html.Substring(open, end - open + 1);
        }
    }

    /// <summary>Index of the ']' closing the '[' at <paramref name="open"/>, or -1.</summary>
    private static int FindArrayEnd(string text, int open)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '[': depth++; break;
                case ']':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }
}

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
                raw = ParserHelper.DecodeEntities(raw ?? "");
                raw = Regex.Replace(raw, @"\s*-\s*XVIDEOS?\.COM\s*$", "", RegexOptions.IgnoreCase);
                info.Title = raw.Trim();
            }

            // Author
            var authorNode = doc.DocumentNode.SelectSingleNode("//a[contains(@href,'/model/') or contains(@href,'/channels/')]");
            if (authorNode != null) info.Author = ParserHelper.DecodeEntities(authorNode.InnerText?.Trim() ?? "");

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

            // 把不透明的 "hls" 展开成真实清晰度档位（720p/480p/360p/250p）
            await ExpandHlsRenditionsAsync(info, ct);
            return info;
        }
        catch { return null; }
    }

    /// <summary>
    /// XVideos 页面只给出一个不透明的 "hls" 地址，真正的清晰度阶梯在主播放列表
    /// 里（实测 720p / 480p / 360p / 250p）。不展开的话用户只能拿到
    /// setVideoUrlHigh 指向的 360p mp4，看起来就是“下载下来分辨率太低”。
    /// </summary>
    private static async Task ExpandHlsRenditionsAsync(VideoInfo info, CancellationToken ct)
    {
        if (!info.StreamUrls.TryGetValue("hls", out var masterUrl) ||
            string.IsNullOrWhiteSpace(masterUrl)) return;

        try
        {
            using var resp = await HttpClientFactory.GetClient().GetAsync(masterUrl, ct);
            if (!resp.IsSuccessStatusCode) return;
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!text.Contains("#EXT-X-STREAM-INF")) return;   // 已经是媒体播放列表

            var baseUri = new Uri(masterUrl);
            var perHeight = ParseMasterRenditions(text, baseUri);
            if (perHeight.Count == 0) return;

            info.StreamUrls.Remove("hls");
            info.Qualities.RemoveAll(q => q.Value == "hls");
            foreach (var r in perHeight)
            {
                var key = $"hls_{r.Height}";
                info.StreamUrls[key] = r.Url;
                info.Qualities.Add(new QualityOption($"{r.Height}p (HLS)", key));
            }
        }
        catch
        {
            // 主播放列表拿不到就保留原来的 "hls" 条目，行为退回改动前
        }
    }

    /// <summary>
    /// 解析 HLS 主播放列表的清晰度档位（按分辨率从高到低）。
    /// 同一分辨率出现多条时只保留码率最高的那条。
    /// 公开是为了让离线测试能直接喂一段 m3u8 文本。
    /// </summary>
    public static List<(int Height, long Bandwidth, string Url)> ParseMasterRenditions(
        string m3u8, Uri baseUri)
    {
        var renditions = new List<(int Height, long Bandwidth, string Url)>();
        var lines = m3u8.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF")) continue;
            if (i + 1 >= lines.Length) continue;

            var uriLine = lines[i + 1].Trim();
            if (uriLine.Length == 0 || uriLine.StartsWith("#")) continue;

            var height = 0;
            var res = Regex.Match(line, @"RESOLUTION=\d+x(\d+)");
            if (res.Success) height = int.Parse(res.Groups[1].Value);
            else
            {
                var name = Regex.Match(line, @"NAME=""(\d+)p""");
                if (name.Success) height = int.Parse(name.Groups[1].Value);
            }
            if (height <= 0) continue;

            var bw = Regex.Match(line, @"BANDWIDTH=(\d+)");
            var bandwidth = bw.Success ? long.Parse(bw.Groups[1].Value) : 0L;
            var url = uriLine.StartsWith("http") ? uriLine : new Uri(baseUri, uriLine).ToString();
            renditions.Add((height, bandwidth, url));
        }

        return renditions
            .GroupBy(r => r.Height)
            .Select(g => g.OrderByDescending(r => r.Bandwidth).First())
            .OrderByDescending(r => r.Height)
            .ToList();
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
        if (highMatch.Success) AddMp4(info, highMatch.Groups[1].Value);

        var lowMatch = Regex.Match(html, @"setVideoUrlLow\('([^']+)'");
        if (lowMatch.Success) AddMp4(info, lowMatch.Groups[1].Value);

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

    /// <summary>
    /// setVideoUrlHigh / setVideoUrlLow 只是“高/低”两个标签，实际上分别是
    /// 360p 和 240p。文件名里带着真实分辨率（video_360p.mp4），用它做 key
    /// 和标签，画质列表才排得对、用户也看得懂。
    /// </summary>
    private static void AddMp4(VideoInfo info, string url)
    {
        var m = Regex.Match(url, @"_(\d{3,4})p\.(?:mp4|3gp)", RegexOptions.IgnoreCase);
        var key = m.Success ? $"mp4_{m.Groups[1].Value}" : "mp4";
        var label = m.Success ? $"{m.Groups[1].Value}p (MP4)" : "MP4";

        if (info.StreamUrls.ContainsKey(key)) return;
        info.StreamUrls[key] = url;
        info.Qualities.Add(new QualityOption(label, key));
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

    /// <summary>Category page URL for a key from GetCategoriesAsync.</summary>
    public static string BuildCategoryUrl(string categoryKey, int page = 1)
        => CategoryParsing.BuildXVideosUrl(categoryKey, page);

    /// <summary>
    /// List XVideos categories from the live /tags page, where the header
    /// sub-list carries the real 38 categories as /c/Slug-ID links.
    /// (/categories now returns 404 — that is what used to force the fake list.)
    /// </summary>
    public async Task<List<CategoryItem>> GetCategoriesAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await HttpClientFactory.GetClient().GetAsync("https://www.xvideos.com/tags", ct);
            if (!resp.IsSuccessStatusCode) return new List<CategoryItem>();
            var html = await resp.Content.ReadAsStringAsync(ct);
            return CategoryParsing.ParseXVideos(html);
        }
        catch
        {
            return new List<CategoryItem>();
        }
    }

    /// <summary>
    /// Browse an XVideos category via its /c/Slug-ID path (the numeric id is
    /// mandatory). Pagination appends "/{page}".
    /// </summary>
    public async Task<List<SearchResult>> BrowseCategoryAsync(string categoryKey, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(categoryKey)) return new List<SearchResult>();

        // A plain keyword (legacy key) still works through search
        if (!categoryKey.StartsWith("/"))
            return await SearchAsync(categoryKey, page, ct);

        try
        {
            var resp = await HttpClientFactory.GetClient().GetAsync(BuildCategoryUrl(categoryKey, page), ct);
            if (!resp.IsSuccessStatusCode) return new List<SearchResult>();
            var html = await resp.Content.ReadAsStringAsync(ct);
            return ParseSearchHtml(html);
        }
        catch
        {
            return new List<SearchResult>();
        }
    }

    // ═══════════════════════════════════════════════════════
    //  INTERNAL HELPERS
    // ═══════════════════════════════════════════════════════

    public static List<SearchResult> ParseSearchHtml(string html)
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

            // Title: 优先用带 title 属性的 <a>（通常是缩略图那个链接），
            // 再退回链接内部的文字。
            //
            // 不能直接拿 linkNode：上面选中的 a.thumb-info 没有 title，它的文字是
            // "标题 11 min" —— 时长会被带进标题，最后变成文件名的一部分。
            var title = linkNode.GetAttributeValue("title", "");
            if (string.IsNullOrWhiteSpace(title))
                title = item.SelectSingleNode(".//a[@title]")?.GetAttributeValue("title", "") ?? "";
            if (string.IsNullOrWhiteSpace(title))
            {
                var titleNode = item.SelectSingleNode(".//p[contains(@class,'title')]")
                                ?? item.SelectSingleNode(".//span[contains(@class,'title')]")
                                ?? item.SelectSingleNode(".//div[contains(@class,'thumb-under')]//a")
                                ?? linkNode;
                title = titleNode.InnerText?.Trim() ?? "";
            }
            title = ParserHelper.DecodeEntities(title);
            title = Regex.Replace(title, "<[^>]*>", "").Trim();
            // 兜底：去掉尾巴上的 "11 min" / "1 h 20 min"
            title = Regex.Replace(title, @"\s+\d+\s*(?:h\s*\d+\s*)?min\.?$", "",
                RegexOptions.IgnoreCase).Trim();

            // Duration
            var durNode = item.SelectSingleNode(".//span[contains(@class,'duration')]")
                            ?? item.SelectSingleNode(".//div[contains(@class,'duration')]");
            var duration = durNode?.InnerText?.Trim() ?? "";

            // Author
            var authorNode = item.SelectSingleNode(".//a[contains(@href,'/model/') or contains(@href,'/channels/')]")
                            ?? item.SelectSingleNode(".//span[contains(@class,'author')]");
            var author = ParserHelper.DecodeEntities(authorNode?.InnerText?.Trim() ?? "");

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

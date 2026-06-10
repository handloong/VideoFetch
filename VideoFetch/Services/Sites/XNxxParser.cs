using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using VideoFetch.Models;

namespace VideoFetch.Services.Sites;

public class XNxxParser : ISiteParser
{
    public SiteType Site => SiteType.XNxx;
    private static readonly HttpClient _client = HttpClientFactory.GetClient();

    public bool CanHandle(string url)
        => !string.IsNullOrWhiteSpace(url) && url.Contains("xnxx.com");

    // ══════════════════════════════════════════════════════
    //  FETCH VIDEO INFO
    // ══════════════════════════════════════════════════════

    public async Task<VideoInfo?> FetchVideoInfoAsync(string url, CancellationToken ct = default)
    {
        var info = new VideoInfo { Url = url, Site = Site };
        try
        {
            var resp = await _client.GetAsync(url, ct);
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
                raw = Regex.Replace(raw, @"\s*-\s*XNXX\.COM\s*$", "", RegexOptions.IgnoreCase);
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
        if (highMatch.Success) { info.StreamUrls["high"] = highMatch.Groups[1].Value; info.Qualities.Add(new QualityOption("High", "high")); }
        var lowMatch = Regex.Match(html, @"setVideoUrlLow\('([^']+)'");
        if (lowMatch.Success) { info.StreamUrls["low"] = lowMatch.Groups[1].Value; info.Qualities.Add(new QualityOption("Low", "low")); }
    }

    // ══════════════════════════════════════════════════════
    //  SEARCH
    // ══════════════════════════════════════════════════════

    public async Task<List<SearchResult>> SearchAsync(string query, int page = 1, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        var url = $"https://www.xnxx.com/?k={Uri.EscapeDataString(query)}&page={page}";
        try
        {
            var resp = await _client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return results;
            var html = await resp.Content.ReadAsStringAsync(ct);
            return ParseSearchHtml(html);
        }
        catch { return results; }
    }

    // ══════════════════════════════════════════════════════
    //  INTERNAL HELPERS
    // ══════════════════════════════════════════════════════

    private static List<SearchResult> ParseSearchHtml(string html)
    {
        var results = new List<SearchResult>();
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        // XNXX uses <div class="thumb-block ..."> for each result
        var items = doc.DocumentNode.SelectNodes("//div[contains(@class,'thumb-block')]")
                    ?? doc.DocumentNode.SelectNodes("//div[contains(@class,'frame-block')]");

        if (items == null) return results;

        foreach (var item in items)
        {
            // Link node — <a> inside thumb-block
            var linkNode = item.SelectSingleNode(".//a[contains(@class,'thumb-info')]")
                            ?? item.SelectSingleNode(".//a[contains(@href,'/video')]");
            if (linkNode == null) continue;

            var href = linkNode.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(href)) continue;
            var url = href.StartsWith("http") ? href : "https://www.xnxx.com" + href;

            // Title: attribute first, then nested element text
            var title = linkNode.GetAttributeValue("title", "") ?? linkNode.GetAttributeValue("alt", "");
            if (string.IsNullOrWhiteSpace(title))
            {
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

            // Author / uploader
            var authorNode = item.SelectSingleNode(".//a[contains(@href,'/model/') or contains(@href,'/channels/')]")
                            ?? item.SelectSingleNode(".//span[contains(@class,'author')]");
            var author = System.Net.WebUtility.HtmlDecode(authorNode?.InnerText?.Trim() ?? "");

            // Thumbnail
            var imgNode = item.SelectSingleNode(".//img");
            var thumb = imgNode?.GetAttributeValue("src", "")
                        ?? imgNode?.GetAttributeValue("data-src", "")
                        ?? imgNode?.GetAttributeValue("data-cover", "");

            results.Add(new SearchResult
            {
                Title = title,
                Url = url,
                Author = author,
                Duration = duration,
                ThumbnailUrl = thumb,
                Site = SiteType.XNxx
            });
        }
        return results;
    }
}

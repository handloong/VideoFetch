using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VideoFetch.Models;
using VideoFetch.Services;

namespace VideoFetch.Test;

/// <summary>
/// Download-path probe: walks the exact production steps for one video per site
/// (search → fetch info → resolve stream → GET playlist/first segment) and
/// prints the HTTP status at every hop, so a 403 can be attributed to a specific
/// request instead of the generic "Failed: Response status code…".
///
/// Run: dotnet run --project VideoFetch.Test -- --dl [keyword]
/// Nothing is written to the output folder; only 1 KB range requests are made.
/// </summary>
internal static class DownloadProbe
{
    public static async Task RunAsync(string keyword)
    {
        var settings = new SettingsService().Load();
        HttpClientFactory.Configure(settings.ProxyEnabled ? settings.ProxyUrl : string.Empty);
        Console.WriteLine($"=== download probe (keyword: \"{keyword}\", proxy: " +
                          $"{(settings.ProxyEnabled ? settings.ProxyUrl : "none")}) ===\n");

        var parsers = new ISiteParser[]
        {
            new Services.Sites.PinSeParser(),
            new Services.Sites.XVideosParser(),
            new Services.Sites.XNxxParser(),
            new Services.Sites.PornHubParser(),
        };

        foreach (var parser in parsers)
            await ProbeSiteAsync(parser, keyword);
    }

    private static async Task ProbeSiteAsync(ISiteParser parser, string keyword)
    {
        Console.WriteLine($"[{parser.Site}]");
        try
        {
            var results = await parser.SearchAsync(keyword);
            if (results.Count == 0)
            {
                Console.WriteLine("  no search results — skipping");
                return;
            }

            var pick = results.First();
            Console.WriteLine($"  video   {Trim(pick.Title, 60)}");
            Console.WriteLine($"  page    {pick.Url}");

            var info = await parser.FetchVideoInfoAsync(pick.Url);
            if (info == null)
            {
                Console.WriteLine("  FAILED: FetchVideoInfoAsync returned null");
                return;
            }

            Console.WriteLine($"  keys    {string.Join(", ", info.StreamUrls.Keys)}");

            var qualityKey = info.StreamUrls.ContainsKey("best") ? "best" : info.Qualities.FirstOrDefault()?.Value ?? "best";
            var streamUrl = await new VideoInfoService().ResolveStreamUrlAsync(info, qualityKey);
            Console.WriteLine($"  quality {qualityKey} → {Trim(streamUrl, 110)}");

            // Hop 1: the stream URL itself
            var (status, reason, length, type, bodyStart) = await GetHeadAsync(streamUrl, info.Url);

            Console.WriteLine($"  HOP1    {status} {reason}  type={type} len={length}");

            if (!IsOk(status))
            {
                Console.WriteLine($"  ^^ 403/4xx on the stream URL  body: {Trim(bodyStart, 160)}");
                Console.WriteLine();
                return;
            }

            // Hop 2: for HLS, the first segment (that is what actually 403s most often)
            if (streamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                bodyStart.Contains("#EXTM3U", StringComparison.Ordinal))
            {
                var playlist = bodyStart;
                if (playlist.Contains("#EXT-X-STREAM-INF"))
                {
                    var sub = PlaylistFirstUri(playlist, streamUrl, wantStreamInf: true);
                    Console.WriteLine($"  HOP1b   sub-playlist → {Trim(sub, 100)}");
                    var (s2, r2, l2, t2, b2) = await GetHeadAsync(sub, info.Url);
                    Console.WriteLine($"  HOP1b   {s2} {r2}  type={t2} len={l2}");
                    if (!IsOk(s2)) { Console.WriteLine(); return; }
                    playlist = b2;
                    streamUrl = sub;
                }

                var seg = PlaylistFirstUri(playlist, streamUrl, wantStreamInf: false);
                if (seg != null)
                {
                    var (s3, r3, l3, t3, _) = await GetHeadAsync(seg, info.Url);
                    Console.WriteLine($"  HOP2    segment {s3} {r3}  type={t3} len={l3}");
                    if (!IsOk(s3)) Console.WriteLine("  ^^ 403/4xx on the HLS segment");
                    else
                    {
                        var bare = await GetHeadAsync(seg, pageUrl: "");
                        Console.WriteLine($"  HOP2-no-referer {bare.status} {bare.reason}  (does the CDN need Referer?)");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
    }

    private static string? PlaylistFirstUri(string playlist, string baseUrl, bool wantStreamInf)
    {
        var lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim()).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (wantStreamInf && lines[i].StartsWith("#EXT-X-STREAM-INF"))
            {
                for (var j = i + 1; j < lines.Count; j++)
                    if (!lines[j].StartsWith("#")) return new Uri(new Uri(baseUrl), lines[j]).ToString();
            }
            if (!wantStreamInf && !lines[i].StartsWith("#"))
                return new Uri(new Uri(baseUrl), lines[i]).ToString();
        }
        return null;
    }

    private static bool IsOk(System.Net.HttpStatusCode s) => (int)s >= 200 && (int)s < 300;

    /// <summary>GET with Range: bytes=0-1023 and print status/reason/headers.</summary>
    private static async Task<(System.Net.HttpStatusCode status, string reason, long length, string type, string body)>
        GetHeadAsync(string url, string pageUrl)
    {
        var client = HttpClientFactory.GetDownloadClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
        if (!string.IsNullOrEmpty(pageUrl))
        {
            req.Headers.Referrer = new Uri(pageUrl);
            req.Headers.TryAddWithoutValidation("Cookie", "platform=pc; ageconfirmed=1; age_verified=1");
        }

        try
        {
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var length = resp.Content.Headers.ContentLength ?? -1;
            var type = resp.Content.Headers.ContentType?.MediaType ?? "-";
            string body;
            try { body = await resp.Content.ReadAsStringAsync(); }
            catch { body = ""; }
            return (resp.StatusCode, resp.ReasonPhrase ?? "", length, type, body.Length > 4096 ? body[..4096] : body);
        }
        catch (Exception ex)
        {
            return (0, ex.Message, -1, "-", "");
        }
    }

    private static string Trim(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}

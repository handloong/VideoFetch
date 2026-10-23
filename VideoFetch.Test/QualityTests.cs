using System;
using System.Linq;
using VideoFetch.Models;
using VideoFetch.Services;
using VideoFetch.Services.Sites;

namespace VideoFetch.Test;

/// <summary>
/// 画质选择相关的离线测试。
///
/// 线上 bug：XVideos 分类页下载出来的片子只有 360p。原因有两层：
///   1. 站点只给一个不透明的 "hls" 地址，真正的 720p 藏在主播放列表里；
///   2. 画质排序的启发式把 "high"（其实就是 360p mp4）打了 1000 分，
///      高过任何具体分辨率，于是 ResolveQualityKey("best") 选了 360p。
/// </summary>
internal static class QualityTests
{
    private static int _failed;

    public static bool Run()
    {
        Console.WriteLine("\n[quality]");
        var before = _failed;

        Safe("best-rank", BestRanking);
        Safe("hls-master", MasterPlaylistParsing);
        Safe("pinse-id", PinSeVideoId);

        return _failed == before;
    }

    private static void Safe(string name, Action test)
    {
        try { test(); }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
    }

    private static VideoInfo WithStreams(params string[] keys)
    {
        var info = new VideoInfo { Url = "https://example.test/v/1", Site = SiteType.XVideos };
        foreach (var k in keys) info.StreamUrls[k] = "https://cdn.test/" + k.Replace('_', '/');
        return info;
    }

    // ══════════════════════════════════════════════════════════
    //  1. 「最佳画质」到底选了谁
    // ══════════════════════════════════════════════════════════

    private static void BestRanking()
    {
        // 展开后的真实 XVideos 画质集合
        var xv = WithStreams("hls_720", "hls_480", "mp4_360", "mp4_240", "hls_360", "hls_250");
        Check("best: 720p 胜过 480p/360p/240p",
            VideoInfoService.ResolveQualityKey(xv, "best") == "hls_720",
            VideoInfoService.ResolveQualityKey(xv, "best"));
        Check("worst: 最低是 240p",
            VideoInfoService.ResolveQualityKey(xv, "worst") == "mp4_240",
            VideoInfoService.ResolveQualityKey(xv, "worst"));

        // 回归：改动前 high=1000 > hls_720=720，best 会选到 360p 的 mp4
        var mixed = WithStreams("hls_720", "high", "low");
        Check("best: 字面量 high 不能盖过 720p（线上 360p bug）",
            VideoInfoService.ResolveQualityKey(mixed, "best") == "hls_720",
            VideoInfoService.ResolveQualityKey(mixed, "best"));

        // 未展开的 "hls"（站点只给一个地址时）仍应优先于 high/low
        var unexpanded = WithStreams("hls", "high", "low");
        Check("best: 未展开的 hls 优先于 high/low",
            VideoInfoService.ResolveQualityKey(unexpanded, "best") == "hls",
            VideoInfoService.ResolveQualityKey(unexpanded, "best"));

        // 只有 high/low 时不能退化成 low
        var legacy = WithStreams("high", "low");
        Check("best: high > low", VideoInfoService.ResolveQualityKey(legacy, "best") == "high");
        Check("worst: low < high", VideoInfoService.ResolveQualityKey(legacy, "worst") == "low");

        // 显式指定画质必须被尊重
        Check("explicit: 用户选了 480p 就下 480p",
            VideoInfoService.ResolveQualityKey(xv, "hls_480") == "hls_480");
    }

    // ══════════════════════════════════════════════════════════
    //  2. HLS 主播放列表 → 清晰度档位
    // ══════════════════════════════════════════════════════════

    private const string MasterPlaylist = """
        #EXTM3U
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=763904,RESOLUTION=854x480,NAME="480p"
        hls-480p-459fb.m3u8
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=1327104,RESOLUTION=1280x720,NAME="720p"
        hls-720p-c97ca.m3u8
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=423936,RESOLUTION=640x360,NAME="360p"
        hls-360p-b9124.m3u8
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=155648,RESOLUTION=444x250,NAME="250p"
        hls-250p-379b.m3u8
        """;

    private static void MasterPlaylistParsing()
    {
        var baseUri = new Uri("https://hls-cdn77.xvideos-cdn.com/abc==,123/video/0/hls.m3u8");
        var r = XVideosParser.ParseMasterRenditions(MasterPlaylist, baseUri);

        Check("hls-master: 解析出 4 档", r.Count == 4, r.Count.ToString());
        Check("hls-master: 按分辨率从高到低", r.Select(x => x.Height).SequenceEqual(new[] { 720, 480, 360, 250 }),
            string.Join(",", r.Select(x => x.Height)));
        Check("hls-master: 相对地址补全为绝对地址",
            r[0].Url == "https://hls-cdn77.xvideos-cdn.com/abc==,123/video/0/hls-720p-c97ca.m3u8",
            r[0].Url);
        Check("hls-master: 带宽正确", r[0].Bandwidth == 1327104, r[0].Bandwidth.ToString());

        // 同一分辨率多条 → 只留码率最高那条
        var dupes = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=1280x720
            a.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720
            b.m3u8
            """;
        var d = XVideosParser.ParseMasterRenditions(dupes, baseUri);
        Check("hls-master: 同分辨率取高码率", d.Count == 1 && d[0].Url.EndsWith("b.m3u8"), d[0].Url);

        // 只有 NAME 没有 RESOLUTION
        var nameOnly = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1000,NAME="1080p"
            c.m3u8
            """;
        var n = XVideosParser.ParseMasterRenditions(nameOnly, baseUri);
        Check("hls-master: 没有 RESOLUTION 时用 NAME", n.Count == 1 && n[0].Height == 1080);

        // 不是主播放列表（没有 STREAM-INF）→ 空列表，调用方保持原样
        var media = "#EXTM3U\n#EXTINF:10,\nseg1.ts\n";
        Check("hls-master: 媒体播放列表返回空", XVideosParser.ParseMasterRenditions(media, baseUri).Count == 0);
    }

    // ══════════════════════════════════════════════════════════
    //  3. PinSe 视频 id（播放接口要用它拼地址）
    // ══════════════════════════════════════════════════════════

    private static void PinSeVideoId()
    {
        Check("pinse-id: /v/470704", PinSeParser.ExtractVideoId("https://91pinse.com/v/470704") == "470704");
        Check("pinse-id: 带查询串", PinSeParser.ExtractVideoId("https://91pinse.com/v/470704?from=search#t") == "470704");
        Check("pinse-id: 非视频页返回 null", PinSeParser.ExtractVideoId("https://91pinse.com/v/search?keyword=x") == null);
    }

    private static void Check(string name, bool condition, string? actual = null)
    {
        Console.WriteLine(condition
            ? $"  PASS  {name}"
            : $"  FAIL  {name}" + (actual is null ? "" : $"  (实际: {actual})"));
        if (!condition) _failed++;
    }
}

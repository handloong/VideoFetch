using System;
using System.Linq;
using VideoFetch.Services.Sites;

namespace VideoFetch.Test;

/// <summary>
/// 详情页解析（FetchVideoInfoAsync 的纯解析部分）的离线测试。
///
/// 这块之前是**零覆盖**：解析器直接在 HTTP 方法里做 HTML → VideoInfo，
/// 没有夹具也没有接缝，改坏了只能靠联网 --info 才看得见。
/// 现在把解析抽成 ParseDetailHtml（纯函数），这里盯三类真实翻车点：
///   1. &lt;title&gt; / &lt;span class="duration"&gt; 没有 content 属性
///      （GetAttributeValue 缺省返回 ""，用 ?? 兜底是死代码）
///   2. flashvars 里 quality 是数组 → 画质 key 变成 mp4_[]
///   3. flashvars 是畸形 JSON → 不能连带把标题/时长一起搞丢
/// </summary>
internal static class DetailParsingTests
{
    private static int _failed;

    public static bool Run()
    {
        Console.WriteLine("\n[detail-parsing]");
        var before = _failed;

        Safe("xvideos-title", XVideosTitleFromTitleTag);
        Safe("xvideos-duration", XVideosDurationWithoutContent);
        Safe("xvideos-quality", XVideosQualityArray);
        Safe("xnxx", XNxxDetail);
        Safe("pornhub", PornHubBadJson);
        Safe("pinse-title", PinSeTitleWithBackslash);
        Safe("relative-url", RelativeUrlsJoinCorrectly);

        return _failed == before;
    }

    /// <summary>页面只有 &lt;title&gt;（没有 og:title）时标题不能是空串。</summary>
    private static void XVideosTitleFromTitleTag()
    {
        const string html = "<html><head><title>Test Video Title - XVIDEOS.COM</title></head><body></body></html>";
        var info = XVideosParser.ParseDetailHtml(html, "https://www.xvideos.com/video.abc/x");

        Check("xvideos: 没有 og:title 时用 <title>", info.Title == "Test Video Title", info.Title);
        Check("xvideos: 标题去掉了站点后缀", !info.Title.Contains("XVIDEOS", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>列表风格的 &lt;span class="duration"&gt; 只有文本，没有 content 属性。</summary>
    private static void XVideosDurationWithoutContent()
    {
        const string html = "<html><body><span class=\"duration\">11 min</span></body></html>";
        var info = XVideosParser.ParseDetailHtml(html, "https://www.xvideos.com/video.abc/x");

        Check("xvideos: 时长取 span 的文本", info.Duration == "11 min", info.Duration);
    }

    /// <summary>quality 是数组时不能拼出 mp4_[] 这种 key（下拉框会显示 "[]p"）。</summary>
    private static void XVideosQualityArray()
    {
        const string html = """
            <html><body><script>
            var flashvars_123 = {"mediaDefinitions":[
              {"format":"mp4","quality":[],"videoUrl":"https://cdn.test/a.mp4"},
              {"format":"hls","quality":"720","videoUrl":"https://cdn.test/a.m3u8"}
            ]};
            </script></body></html>
            """;

        var info = XVideosParser.ParseDetailHtml(html, "https://www.xvideos.com/video.abc/x");

        Check("xvideos: quality 数组不会生成 mp4_[]",
            info.StreamUrls.Keys.All(k => !k.Contains("[]")), string.Join(",", info.StreamUrls.Keys));
        Check("xvideos: 数组型 quality 归为 unknown", info.StreamUrls.ContainsKey("mp4_unknown"),
            string.Join(",", info.StreamUrls.Keys));
        Check("xvideos: 正常画质不受影响", info.StreamUrls.ContainsKey("hls_720"));
        Check("xvideos: 画质项不重复", info.Qualities.Count == info.Qualities.Select(q => q.Value).Distinct().Count(),
            info.Qualities.Count.ToString());
    }

    private static void XNxxDetail()
    {
        const string html = """
            <html><head><title>XNxx Clip - XNXX.COM</title></head>
            <body><span class="duration">8min</span>
            <script>var flashvars_x = {"mediaDefinitions":[{"format":"hls","quality":"720","videoUrl":"https://cdn.test/x.m3u8"}]};</script>
            </body></html>
            """;

        var info = XNxxParser.ParseDetailHtml(html, "https://www.xnxx.com/video-abc/x");

        Check("xnxx: 标题来自 <title>", info.Title == "XNxx Clip", info.Title);
        Check("xnxx: 时长来自 span", info.Duration == "8min", info.Duration);
        Check("xnxx: 解析出流", info.StreamUrls.ContainsKey("hls_720"),
            string.Join(",", info.StreamUrls.Keys));
    }

    /// <summary>flashvars 是畸形 JSON 时不能抛，也不能影响其它字段。</summary>
    private static void PornHubBadJson()
    {
        const string broken = """
            <html><head>
              <meta property="og:title" content="PH Title">
              <meta property="og:image" content="https://cdn.test/t.jpg">
            </head><body>
            <script>var flashvars_bad = {"mediaDefinitions":[null,"oops",]};
            </script></body></html>
            """;

        var info = new VideoFetch.Models.VideoInfo { Url = "https://www.pornhub.com/view_video.php?viewkey=x" };
        var threw = false;
        try { PornHubParser.ExtractStreams(broken, info); }
        catch (Exception ex) { threw = true; Console.WriteLine($"  info  ExtractStreams 抛了 {ex.GetType().Name}"); }

        Check("pornhub: 畸形 flashvars 不抛异常", !threw);
        Check("pornhub: 解析不到流时就是空", info.StreamUrls.Count == 0, info.StreamUrls.Count.ToString());

        // 正常 JSON + 数组型 quality 也要能解析
        const string good = """
            <script>var flashvars_ok = {"mediaDefinitions":[
              {"format":"hls","quality":[720],"videoUrl":"https://cdn.test/p.m3u8"}]};
            </script>
            """;
        var info2 = new VideoFetch.Models.VideoInfo();
        PornHubParser.ExtractStreams(good, info2);
        Check("pornhub: 数组型 quality 取第一个元素", info2.StreamUrls.ContainsKey("hls_720"),
            string.Join(",", info2.StreamUrls.Keys));
    }

    /// <summary>标题里有反斜杠：Regex.Unescape 会把 \q 当非法转义抛异常、\b 当退格符。</summary>
    private static void PinSeTitleWithBackslash()
    {
        const string html = "<html><head><meta property=\"og:title\" content=\"a\\qc 100%\\ok\"></head></html>";

        string? title = null;
        var threw = false;
        try { title = PinSeParser.ExtractPageTitle(html); }
        catch (Exception ex) { threw = true; Console.WriteLine($"  info  ExtractPageTitle 抛了 {ex.GetType().Name}: {ex.Message}"); }

        Check("pinse: 标题含 \\q 不抛异常", !threw);
        // 标题会当文件名用，反斜杠这类非法字符会被 SanitizeFileName 换成 _，
        // 所以这里断言"文字内容没丢"，而不是"反斜杠原样保留"
        Check("pinse: 标题文字没丢", title != null && title.Contains("qc") && title.Contains("100%"),
            title ?? "null");
        Check("pinse: 标题里没有退格控制字符",
            title != null && !title.Contains('\b'), title?.Replace("\b", "<BS>") ?? "null");
    }

    /// <summary>
    /// 站内相对链接的三种写法都要拼对。以前只判断 StartsWith("http")，
    /// 协议相对和无前导斜杠的 href 会拼出 host 被污染的非法地址。
    /// </summary>
    private static void RelativeUrlsJoinCorrectly()
    {
        const string baseUrl = "https://www.xnxx.com/";

        Check("url: 绝对地址原样返回",
            ParserHelper.AbsoluteUrl(baseUrl, "https://cdn.test/a.mp4") == "https://cdn.test/a.mp4",
            ParserHelper.AbsoluteUrl(baseUrl, "https://cdn.test/a.mp4"));

        Check("url: 以 / 开头的站内路径",
            ParserHelper.AbsoluteUrl(baseUrl, "/video-abc/x") == "https://www.xnxx.com/video-abc/x",
            ParserHelper.AbsoluteUrl(baseUrl, "/video-abc/x"));

        Check("url: 无前导斜杠也不会污染 host",
            ParserHelper.AbsoluteUrl(baseUrl, "video-abc/x") == "https://www.xnxx.com/video-abc/x",
            ParserHelper.AbsoluteUrl(baseUrl, "video-abc/x"));

        Check("url: 协议相对地址补 https",
            ParserHelper.AbsoluteUrl(baseUrl, "//www.xnxx.com/video-x") == "https://www.xnxx.com/video-x",
            ParserHelper.AbsoluteUrl(baseUrl, "//www.xnxx.com/video-x"));

        Check("url: 空值返回空串", ParserHelper.AbsoluteUrl(baseUrl, "") == "");
    }

    private static void Safe(string name, Action test)
    {
        try { test(); }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}: 抛异常 {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
    }

    private static void Check(string name, bool condition, string? actual = null)
    {
        Console.WriteLine(condition
            ? $"  PASS  {name}"
            : $"  FAIL  {name}" + (actual is null ? "" : $"  (实际: {actual})"));
        if (!condition) _failed++;
    }
}

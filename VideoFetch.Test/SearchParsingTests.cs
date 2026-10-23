using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VideoFetch.Models;
using VideoFetch.Services.Sites;

namespace VideoFetch.Test;

/// <summary>
/// 四个站点搜索结果的离线解析测试。
///
/// 之前完全没有覆盖：解析函数是 private，改一个选择器只能靠联网 --live
/// 才发现回归。现在把真实搜索页裁剪成夹具（每站点 2 个结果块），断言
/// 标题/链接/时长/站点这些字段都能解析出来。
/// </summary>
internal static class SearchParsingTests
{
    private static int _failed;
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static bool Run()
    {
        Console.WriteLine("\n[search-parsing]");
        var before = _failed;

        Safe("xvideos", () => TestSite(
            "xvideos_search.html", SiteType.XVideos,
            html => XVideosParser.ParseSearchHtml(html),
            expectUrlPart: "/video.ufvhohd4fe6/real_sexo_amateur",
            expectTitle: "real sexo amateur"));

        Safe("xnxx", () => TestSite(
            "xnxx_search.html", SiteType.XNxx,
            html => XNxxParser.ParseSearchHtml(html),
            expectUrlPart: "video-z65sf7b",
            expectTitle: "First time Asian amateur fucked by Japan man"));

        Safe("pornhub", () => TestSite(
            "pornhub_search.html", SiteType.PornHub,
            html => PornHubParser.ParseSearchResultsHtml(html),
            expectUrlPart: "viewkey=6a662d7e0cfee",
            expectTitle: "Busty Redhead Rides Him All Night"));

        Safe("pinse", TestPinSe);
        Safe("robustness", Robustness);

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

    private static string Fixture(string name)
    {
        var path = Path.Combine(FixtureDir, name);
        if (!File.Exists(path)) throw new FileNotFoundException($"缺少夹具 {name}", path);
        return File.ReadAllText(path);
    }

    /// <summary>三个基于 HtmlAgilityPack 的站点共用同一组结构断言。</summary>
    private static void TestSite(string fixture, SiteType site,
        Func<string, System.Collections.Generic.List<SearchResult>> parse,
        string expectUrlPart, string expectTitle)
    {
        Console.WriteLine($"-- {site}");
        var results = parse(Fixture(fixture));

        Check($"{site}: 解析出 2 条", results.Count == 2, results.Count.ToString());
        if (results.Count == 0) return;

        var first = results[0];
        Check($"{site}: 标题", first.Title == expectTitle, first.Title);
        Check($"{site}: 链接是绝对地址", first.Url.StartsWith("https://"), first.Url);
        Check($"{site}: 链接内容", first.Url.Contains(expectUrlPart), first.Url);
        Check($"{site}: 站点标记", results.All(r => r.Site == site));
        Check($"{site}: 链接唯一", results.Select(r => r.Url).Distinct().Count() == results.Count);
        Check($"{site}: 缩略图是绝对地址",
            !string.IsNullOrEmpty(first.ThumbnailUrl) && first.ThumbnailUrl.StartsWith("http"),
            first.ThumbnailUrl);
        Check($"{site}: 标题不含未解码的 HTML 标签",
            results.All(r => !r.Title.Contains('<')));
    }

    /// <summary>
    /// 91pinse 是正则解析，标题里带 &lt;mark&gt; 高亮标签要清掉，时长必须解析出来。
    /// </summary>
    private static void TestPinSe()
    {
        Console.WriteLine("-- PinSe");
        var results = PinSeParser.ParseSearchHtml(Fixture("pinse_search.html"));

        Check("pinse: 解析出 2 条", results.Count == 2, results.Count.ToString());
        if (results.Count == 0) return;

        var first = results[0];
        Check("pinse: 标题已去掉 mark 高亮", !first.Title.Contains("<mark"), first.Title);
        Check("pinse: 标题内容", first.Title.Contains("去隔壁"), first.Title);
        Check("pinse: 链接是绝对地址", first.Url.StartsWith("https://91pinse.com/v/"), first.Url);
        Check("pinse: 时长", first.Duration == "0:02:14", first.Duration);
        Check("pinse: 站点标记", results.All(r => r.Site == SiteType.PinSe));
        Check("pinse: 缩略图是绝对地址", first.ThumbnailUrl.StartsWith("https://"), first.ThumbnailUrl);
    }

    /// <summary>垃圾输入不能抛异常，返回空列表即可。</summary>
    private static void Robustness()
    {
        Console.WriteLine("-- robustness");
        var junk = "<html><body><div>没有任何视频</div></body></html>";

        Check("robustness: XVideos 空页面返回 0",
            XVideosParser.ParseSearchHtml(junk).Count == 0);
        Check("robustness: XNxx 空页面返回 0",
            XNxxParser.ParseSearchHtml(junk).Count == 0);
        Check("robustness: PornHub 空页面返回 0",
            PornHubParser.ParseSearchResultsHtml(junk).Count == 0);
        Check("robustness: PinSe 空页面返回 0",
            PinSeParser.ParseSearchHtml(junk).Count == 0);

        Check("robustness: 空字符串不抛异常",
            XVideosParser.ParseSearchHtml("").Count == 0 &&
            PinSeParser.ParseSearchHtml("").Count == 0);

        // 时长格式：允许 "0:02:14" / "12:34"，不允许空串混进有效结果
        var durations = PinSeParser.ParseSearchHtml(Fixture("pinse_search.html"))
            .Select(r => r.Duration).ToList();
        Check("robustness: 时长格式正确",
            durations.All(d => Regex.IsMatch(d, @"^\d+(:\d+){1,2}$")),
            string.Join(",", durations));
    }

    private static void Check(string name, bool condition, string? actual = null)
    {
        Console.WriteLine(condition
            ? $"  PASS  {name}"
            : $"  FAIL  {name}" + (actual is null ? "" : $"  (实际: {actual})"));
        if (!condition) _failed++;
    }
}

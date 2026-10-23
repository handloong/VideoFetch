using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VideoFetch.Models;
using VideoFetch.Services;
using VideoFetch.Services.Sites;

namespace VideoFetch.Test;

/// <summary>
/// Offline category-parsing tests.
///
/// The fixtures under Fixtures/ are trimmed copies of the *real* HTML served by
/// each site (captured 2026-10-23). They pin the markup contract, so a silent
/// site redesign shows up as a failing test instead of "categories look wrong"
/// in the UI. No network is required.
/// </summary>
internal static class CategoryTests
{
    private static int _pass;
    private static int _fail;

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static string Fixture(string name)
    {
        var path = Path.Combine(FixtureDir, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"fixture missing: {path}");
        return File.ReadAllText(path);
    }

    // ── assertions ───────────────────────────────────────────────
    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _pass++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  FAIL  {name}{(detail == null ? "" : " — " + detail)}");
        }
    }

    private static void Equal<T>(string name, T expected, T actual)
        => Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"expected [{expected}] got [{actual}]");

    private static void AtLeast(string name, int minimum, int actual)
        => Check(name, actual >= minimum, $"expected >= {minimum}, got {actual}");

    // ── shared invariants ───────────────────────────────────────
    private static void CheckCommonInvariants(string label, List<CategoryItem> items, SiteType site)
    {
        Check($"{label}: every item has a display name", items.All(c => !string.IsNullOrWhiteSpace(c.Name)));
        Check($"{label}: every item has a browse key", items.All(c => !string.IsNullOrWhiteSpace(c.Key)));
        Check($"{label}: every item carries its site", items.All(c => c.Site == site));
        Check($"{label}: no duplicate keys",
            items.Select(c => c.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == items.Count);
        Check($"{label}: no placeholder keys",
            items.All(c => !c.Key.Contains('{') && !c.Key.Contains('}')));
        // Regression: the old XNXX parser fed the A–Z index into the list, so the
        // UI showed categories literally named "A", "B", "C"…
        Check($"{label}: no single-letter pseudo categories",
            items.All(c => c.Name.Trim().Trim('#').Length > 1 || c.Name.Trim().Any(char.IsDigit)));
    }

    // ══════════════════════════════════════════════════════════
    //  XVideos
    // ══════════════════════════════════════════════════════════
    private static void TestXVideos()
    {
        Console.WriteLine("\n[XVideos]");
        var items = CategoryParsing.ParseXVideos(Fixture("xvideos_tags.html"));

        Equal("xvideos: 38 real categories", 38, items.Count);
        AtLeast("xvideos: category count sane", 20, items.Count);
        CheckCommonInvariants("xvideos", items, SiteType.XVideos);
        Check("xvideos: every key is a /c/ path", items.All(c => c.Key.StartsWith("/c/")));

        var amateur = items.FirstOrDefault(c => c.Name == "Amateur");
        Check("xvideos: Amateur category found", amateur != null);
        Equal("xvideos: Amateur key carries the site id", "/c/Amateur-65", amateur?.Key ?? "");
        Check("xvideos: Big Ass kept its space", items.Any(c => c.Name == "Big Ass" && c.Key == "/c/Big_Ass-24"));

        Equal("xvideos: first page url", "https://www.xvideos.com/c/Amateur-65",
            XVideosParser.BuildCategoryUrl("/c/Amateur-65", 1));
        Equal("xvideos: page 2 url", "https://www.xvideos.com/c/Amateur-65/2",
            XVideosParser.BuildCategoryUrl("/c/Amateur-65", 2));

        Equal("xvideos: markup change yields empty list", 0,
            CategoryParsing.ParseXVideos("<html><body><p>no categories here</p></body></html>").Count);
    }

    // ══════════════════════════════════════════════════════════
    //  PornHub
    // ══════════════════════════════════════════════════════════
    private static void TestPornHub()
    {
        Console.WriteLine("\n[PornHub]");
        var items = CategoryParsing.ParsePornHub(Fixture("pornhub_categories.html"));

        AtLeast("pornhub: full category grid parsed", 100, items.Count);
        // The page repeats its 10 "top categories" in a hero strip before the
        // full grid (115 tiles → 105 distinct categories), so dedupe applies here.
        Equal("pornhub: 105 unique categories from 115 tiles", 105, items.Count);
        CheckCommonInvariants("pornhub", items, SiteType.PornHub);
        Check("pornhub: keys are numeric category ids", items.All(c => c.Key.All(char.IsDigit)));

        var mature = items.FirstOrDefault(c => c.Name == "Mature");
        Check("pornhub: Mature category found", mature != null);
        Equal("pornhub: Mature id", "28", mature?.Key ?? "");
        Equal("pornhub: Mature video count", 48862, mature?.VideoCount ?? 0);
        Check("pornhub: names have no 'Videos' suffix",
            items.All(c => !c.Name.Contains("Videos", StringComparison.OrdinalIgnoreCase)));

        Equal("pornhub: first page url", "https://www.pornhub.com/video?c=28",
            PornHubParser.BuildCategoryUrl("28", 1));
        Equal("pornhub: page 3 url", "https://www.pornhub.com/video?c=28&page=3",
            PornHubParser.BuildCategoryUrl("28", 3));

        Equal("pornhub: markup change yields empty list", 0,
            CategoryParsing.ParsePornHub("<html><body>nope</body></html>").Count);
    }

    // ══════════════════════════════════════════════════════════
    //  XNXX
    // ══════════════════════════════════════════════════════════
    private static void TestXNxx()
    {
        Console.WriteLine("\n[XNXX]");
        var items = CategoryParsing.ParseXNxx(Fixture("xnxx_tags.html"));

        Equal("xnxx: 158 categories from embedded json", 158, items.Count);
        CheckCommonInvariants("xnxx", items, SiteType.XNxx);
        Check("xnxx: keys point at /search/ pages",
            items.All(c => c.Key.StartsWith("/search/") || c.Key.StartsWith("/trans") || c.Key.StartsWith("/gay") || c.Key.StartsWith("/lesbian") || c.Key.StartsWith("/gay")));

        var toons = items.FirstOrDefault(c => c.Name == "Toons");
        Check("xnxx: Toons category found", toons != null);
        Equal("xnxx: Toons key", "/search/toons", toons?.Key ?? "");
        Check("xnxx: multi-word label kept", items.Any(c => c.Name == "Solo Girls" && c.Key == "/search/solo_and_masturbation"));
        Check("xnxx: no A–Z index leaked in", !items.Any(c => c.Name.Trim().Length == 1 && !char.IsDigit(c.Name.Trim()[0])));

        Equal("xnxx: first page url", "https://www.xnxx.com/search/toons",
            XNxxParser.BuildCategoryUrl("/search/toons", 1));
        Equal("xnxx: page 2 url", "https://www.xnxx.com/search/toons/2",
            XNxxParser.BuildCategoryUrl("/search/toons", 2));

        Equal("xnxx: markup change yields empty list", 0,
            CategoryParsing.ParseXNxx("<html><body>no json</body></html>").Count);
    }

    // ══════════════════════════════════════════════════════════
    //  PinSe (91pinse)
    // ══════════════════════════════════════════════════════════
    private static void TestPinSe()
    {
        Console.WriteLine("\n[PinSe]");
        var items = CategoryParsing.ParsePinSe(Fixture("pinse_v_categories.html"));

        AtLeast("pinse: 热门搜索 tag cloud parsed", 90, items.Count);
        CheckCommonInvariants("pinse", items, SiteType.PinSe);
        Check("pinse: keys are plain keywords", items.All(c => !c.Key.StartsWith("/") && !c.Key.StartsWith("http")));
        Check("pinse: 熟女 present", items.Any(c => c.Name == "熟女"));
        Check("pinse: 高颜值 present", items.Any(c => c.Name == "高颜值"));
        Check("pinse: latin tag 3p present", items.Any(c => c.Name.Equals("3p", StringComparison.OrdinalIgnoreCase)));

        var chips = CategoryParsing.ParsePinSe(Fixture("pinse_home_categories.html"));
        Equal("pinse: homepage 热门 chips parsed", 12, chips.Count);
        Equal("pinse: chip name from title attribute", "熟女", chips.FirstOrDefault()?.Name ?? "");
        Equal("pinse: chip key is the keyword", "熟女", chips.FirstOrDefault()?.Key ?? "");

        Equal("pinse: first page url",
            "https://91pinse.com/v/search?keyword=%E7%86%9F%E5%A5%B3",
            PinSeParser.BuildCategoryUrl("熟女", 1));
        Equal("pinse: page 2 url",
            "https://91pinse.com/v/search?keyword=%E7%86%9F%E5%A5%B3&page=2",
            PinSeParser.BuildCategoryUrl("熟女", 2));

        Equal("pinse: empty html yields empty list", 0,
            CategoryParsing.ParsePinSe("<html><body><div>nothing</div></body></html>").Count);

        TestPinSeDuration();
    }

    /// <summary>
    /// 91pinse 的时长筛选走站点的 ?dur= 参数：
    /// 空=全部 / short(&lt;5 分钟) / medium(5-20) / long(20+)。
    /// </summary>
    private static void TestPinSeDuration()
    {
        Console.WriteLine("-- pinse 时长筛选");

        Equal("duration: All 不带参数", "", CategoryParsing.DurationParam(DurationFilter.All));
        Equal("duration: Short", "short", CategoryParsing.DurationParam(DurationFilter.Short));
        Equal("duration: Medium", "medium", CategoryParsing.DurationParam(DurationFilter.Medium));
        Equal("duration: Long", "long", CategoryParsing.DurationParam(DurationFilter.Long));

        // 全部时长必须保持和改动前一模一样的 URL（不能冒出空参数 &dur=）
        Equal("duration: All 时 URL 不变",
            "https://91pinse.com/v/search?keyword=%E7%86%9F%E5%A5%B3",
            PinSeParser.BuildCategoryUrl("熟女", 1, DurationFilter.All));

        Equal("duration: Long 追加 &dur=long",
            "https://91pinse.com/v/search?keyword=%E7%86%9F%E5%A5%B3&dur=long",
            PinSeParser.BuildCategoryUrl("熟女", 1, DurationFilter.Long));

        Equal("duration: 翻页 + 时长同时生效",
            "https://91pinse.com/v/search?keyword=%E7%86%9F%E5%A5%B3&page=2&dur=medium",
            PinSeParser.BuildCategoryUrl("熟女", 2, DurationFilter.Medium));

        // 能力声明：界面据此决定是否显示下拉框。
        // 默认实现挂在 ISiteParser 上，所以必须通过接口看，不能看具体类。
        ISiteParser pinse = new PinSeParser();
        ISiteParser porn = new PornHubParser();
        ISiteParser xv = new XVideosParser();
        ISiteParser xn = new XNxxParser();
        Check("duration: 只有 91pinse 声明支持",
            pinse.SupportsDurationFilter
            && !porn.SupportsDurationFilter
            && !xv.SupportsDurationFilter
            && !xn.SupportsDurationFilter);
    }

    // ══════════════════════════════════════════════════════════
    //  robustness
    // ══════════════════════════════════════════════════════════
    private static void TestRobustness()
    {
        Console.WriteLine("\n[robustness]");

        // A duplicate-heavy page must collapse to unique keys.
        var html = "<html><body>" +
                   "<li class=\"dyntop-cat top-cat-1\"><a href=\"/c/Amateur-65\">Amateur</a></li>" +
                   "<li class=\"dyntop-cat top-cat-1\"><a href=\"/c/Amateur-65\">Amateur</a></li>" +
                   "<li class=\"dyntop-cat top-cat-2\"><a href=\"/c/Anal-12\">Anal</a></li>" +
                   "</body></html>";
        var deduped = CategoryParsing.ParseXVideos(html);
        Equal("dedupe: duplicates collapsed", 2, deduped.Count);

        // Empty / null html must not throw.
        Check("null html is safe",
            CategoryParsing.ParsePornHub("").Count == 0
            && CategoryParsing.ParseXNxx("").Count == 0
            && CategoryParsing.ParsePinSe("").Count == 0
            && CategoryParsing.ParseXVideos("").Count == 0);

        // Whitespace/entity noise must be cleaned.
        var noisy = "<html><body><li class=\"dyntop-cat\"><a href=\"/c/Big_Ass-24\">\n   Big&nbsp;Ass \n</a></li></body></html>";
        var cleaned = CategoryParsing.ParseXVideos(noisy);
        Equal("cleanup: entity + whitespace normalised", "Big Ass", cleaned.FirstOrDefault()?.Name ?? "");
    }

    public static bool Run()
    {
        _pass = 0;
        _fail = 0;
        Console.WriteLine($"=== Category tests (fixtures: {FixtureDir}) ===");

        TestXVideos();
        TestPornHub();
        TestXNxx();
        TestPinSe();
        TestRobustness();
        TestTextDecoding();

        Console.WriteLine($"\n=== {_pass} passed, {_fail} failed ===");
        return _fail == 0;
    }

    /// <summary>
    /// 标题文本解码：站点把标题二次转义，页面里出现 &amp;amp;comma; 这种形式，
    /// 只解一遍会把 "&amp;comma;" 原样显示在下载列表的标题列上。
    /// </summary>
    private static void TestTextDecoding()
    {
        Console.WriteLine("\n-- text decoding");

        Check("entities: 普通命名实体",
            ParserHelper.DecodeEntities("Tom &amp; Jerry") == "Tom & Jerry");
        Check("entities: 二次转义的 &comma;",
            ParserHelper.DecodeEntities("Sex with a tight ass&amp;comma; homet") == "Sex with a tight ass, homet",
            ParserHelper.DecodeEntities("Sex with a tight ass&amp;comma; homet"));
        Check("entities: 数值实体",
            ParserHelper.DecodeEntities("A&#44; B") == "A, B");
        Check("entities: 不含实体的 & 不误伤",
            ParserHelper.DecodeEntities("AT&T and R&D") == "AT&T and R&D");
        Check("entities: 干净文本保持不变",
            ParserHelper.DecodeEntities("plain title 123") == "plain title 123");
        Check("entities: null/空值安全",
            ParserHelper.DecodeEntities(null) == "" && ParserHelper.DecodeEntities("") == "");
    }
}

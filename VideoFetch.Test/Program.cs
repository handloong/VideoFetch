using System;
using System.Linq;
using System.Threading.Tasks;
using VideoFetch.Services;
using VideoFetch.Services.Sites;
using VideoFetch.Test;

// 控制台输出统一 UTF-8：断言名都是中文，GBK 控制台里会变成乱码，抓 FAIL 都抓不准
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

// Test entry point.
//   (no args)  offline only: category parsing (fixtures) + download queue state machine
//              + CategoryTab / QueueTab layout smoke tests
//   --cats     + live: load each site's real categories and browse the first two
//   --live     + live: categories and the 4-site search/parse integration test
//   --probe    network only: print HTTP status for the pages the parsers depend on
var live = args.Any(a => a.Equals("--live", StringComparison.OrdinalIgnoreCase));
var catsOnly = args.Any(a => a.Equals("--cats", StringComparison.OrdinalIgnoreCase));
var probeOnly = args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase));
var dlIndex = Array.FindIndex(args, a => a.Equals("--dl", StringComparison.OrdinalIgnoreCase));
if (probeOnly)
{
    await NetProbe.RunAsync();
    return;
}

// --info <url>: 打印解析出的画质列表（顺序 + 最佳画质解析结果），调试下载画质用
var infoIndex = Array.FindIndex(args, a => a.Equals("--info", StringComparison.OrdinalIgnoreCase));
if (infoIndex >= 0 && infoIndex + 1 < args.Length)
{
    await DownloadProbe.ProbeUrlAsync(args[infoIndex + 1]);
    return;
}

// --dur [keyword]: 打印四站点搜索结果的时长（原始文案 + 秒数 + 长→短排序结果）
var durIndex = Array.FindIndex(args, a => a.Equals("--dur", StringComparison.OrdinalIgnoreCase));
if (durIndex >= 0)
{
    var kw = durIndex + 1 < args.Length && !args[durIndex + 1].StartsWith("--")
        ? args[durIndex + 1]
        : "amateur";
    await DurationProbe.RunAsync(kw);
    return;
}

if (dlIndex >= 0)
{
    var kw = dlIndex + 1 < args.Length ? args[dlIndex + 1] : "teen";
    await DownloadProbe.RunAsync(kw);
    return;}

var categoriesOk = CategoryTests.Run();
var downloadOk = await DownloadQueueTests.RunAsync();
var engineOk = DownloadEngineTests.Run();
var searchOk = SearchParsingTests.Run();
var detailOk = DetailParsingTests.Run();
var durationOk = DurationSortTests.Run();
var recordOk = DownloadRecordTests.Run();
var qualityOk = QualityTests.Run();
var logOk = AppLogTests.Run();
var cookieOk = SiteCookiesTests.Run();
var routingOk = UrlRoutingTests.Run();
var settingsOk = SettingsServiceTests.Run();
var uiOk = UiSmokeTest.Run();

if (!live && !catsOnly)
{
    Console.WriteLine("\n(skipping live site tests — pass --live to enable)");
    Environment.ExitCode = categoriesOk && downloadOk && engineOk && searchOk && detailOk && durationOk && recordOk && qualityOk && logOk && cookieOk && routingOk && settingsOk && uiOk ? 0 : 1;
    return;
}

var liveCategoriesOk = await CategoryLiveTests.RunAsync();

if (catsOnly)
{
    Environment.ExitCode = categoriesOk && downloadOk && engineOk && searchOk && detailOk && durationOk && recordOk && qualityOk && logOk && cookieOk && routingOk && settingsOk && uiOk && liveCategoriesOk ? 0 : 1;
    return;
}

Console.WriteLine("\n=== Live search integration test ===\n");

var keyword = "teen";
string Trunc(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max];

async Task TestSearch(ISiteParser parser)
{
    try
    {
        var results = await parser.SearchAsync(keyword);
        Console.WriteLine($"[{parser.Site}] Found {results.Count} results");

        if (results.Count > 0)
        {
            foreach (var r in results.Take(3))
                Console.WriteLine($"    -> {Trunc(r.Title, 50)} | {Trunc(r.Url, 60)} | {r.Duration} | {r.Author}");

            if (results.Count > 3)
                Console.WriteLine($"    ... +{results.Count - 3} more results");

            if (!string.IsNullOrEmpty(results[0].Url))
            {
                Console.WriteLine($"\n[{parser.Site}] Fetching video info from first result...");
                var info = await parser.FetchVideoInfoAsync(results[0].Url);
                if (info != null)
                {
                    Console.WriteLine($"    Title: {info.Title}");
                    Console.WriteLine($"    Duration: {info.Duration}");
                    Console.WriteLine($"    Author: {info.Author}");
                    Console.WriteLine($"    Qualities: {info.Qualities.Count}");
                    foreach (var q in info.Qualities)
                        Console.WriteLine($"      - {q.Label} => {q.Value}");
                }
                else
                    Console.WriteLine("    FAILED: could not fetch video info");
            }
        }

        var cats = await parser.GetCategoriesAsync();
        Console.WriteLine($"[{parser.Site}] Categories: {cats.Count}" +
                          (cats.Count > 0 ? $" (first: {string.Join(", ", cats.Take(6).Select(c => c.Name))})" : ""));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{parser.Site}] ERROR: {ex.Message}");
    }
}

await Task.WhenAll(
    TestSearch(new PornHubParser()),
    TestSearch(new XVideosParser()),
    TestSearch(new XNxxParser()),
    TestSearch(new PinSeParser())
);

Console.WriteLine("\nDone!");
Environment.ExitCode = categoriesOk && downloadOk && liveCategoriesOk ? 0 : 1;

using System;
using System.Linq;
using System.Threading.Tasks;
using VideoFetch.Models;
using VideoFetch.Services;

namespace VideoFetch.Test;

/// <summary>
/// 时长解析的联网诊断：抓四个站点的真实搜索页，打印每条结果的原始时长文案、
/// 解析出的秒数，以及"按长→短排序"后的前三条。
///
/// 存在的原因：时长的写法各站不同（"10:52" / "11 min" / "11min" / "0:02:14"），
/// 夹具只能证明"昨天是那样"，站点改版后还是得看真页面。
///
/// Run: dotnet run --project VideoFetch.Test -- --dur [keyword]
/// </summary>
internal static class DurationProbe
{
    public static async Task RunAsync(string keyword)
    {
        var settings = new SettingsService().Load();
        HttpClientFactory.Configure(
            settings.ProxyEnabled ? settings.ProxyUrl : string.Empty,
            settings.SiteCookies);

        var service = new VideoInfoService();

        foreach (var site in new[] { SiteType.PornHub, SiteType.XVideos, SiteType.XNxx, SiteType.PinSe })
        {
            Console.WriteLine($"\n=== {site}  (keyword: {keyword})");
            try
            {
                var results = await service.SearchAsync(site, keyword);
                if (results.Count == 0)
                {
                    Console.WriteLine("  没有结果（可能被站点限流或需要代理）");
                    continue;
                }

                var known = results.Count(r => r.DurationSeconds > 0);
                Console.WriteLine($"  {results.Count} 条，时长解析成功 {known} 条");

                foreach (var r in results.Take(5))
                    Console.WriteLine($"    {r.DurationSeconds,6}s  '{r.Duration}'  {Clip(r.Title)}");

                Console.WriteLine("  按长→短排序后前三：");
                foreach (var r in ResultSorting.Order(results, ResultSort.DurationDesc).Take(3))
                    Console.WriteLine($"    {r.DurationSeconds,6}s  {Clip(r.Title)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  失败: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static string Clip(string text)
        => text.Length <= 60 ? text : text[..60] + "…";
}

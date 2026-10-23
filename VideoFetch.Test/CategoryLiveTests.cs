using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VideoFetch.Models;
using VideoFetch.Services;
using VideoFetch.Services.Sites;

namespace VideoFetch.Test;

/// <summary>
/// Live (network) category test: loads each site's real category list through
/// the production code path and then browses the first category, proving the
/// keys actually resolve to a page with videos. Run with --live.
///
/// Uses the same proxy settings as the app (%AppData%\VideoFetch\settings.json),
/// so a machine that can reach the sites in the app can also run this test.
/// </summary>
internal static class CategoryLiveTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {name}{(detail == null ? "" : " — " + detail)}"); }
    }

    public static async Task<bool> RunAsync()
    {
        _pass = 0;
        _fail = 0;

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoFetch", "settings.json");

        if (File.Exists(settingsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(settingsPath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                HttpClientFactory.Configure(settings.ProxyEnabled ? settings.ProxyUrl : string.Empty);
                Console.WriteLine($"=== Live category tests (proxy: {(settings.ProxyEnabled ? settings.ProxyUrl : "none")}) ===");
            }
            catch { /* fall back to no proxy */ }
        }

        var parsers = new ISiteParser[]
        {
            new PinSeParser(), new XVideosParser(), new XNxxParser(), new PornHubParser()
        };

        foreach (var parser in parsers)
            await TestSiteAsync(parser);

        Console.WriteLine($"\n=== {_pass} passed, {_fail} failed ===");
        return _fail == 0;
    }

    private static async Task TestSiteAsync(ISiteParser parser)
    {
        Console.WriteLine($"\n[{parser.Site}]");
        try
        {
            List<CategoryItem> categories;
            using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90)))
                categories = await parser.GetCategoriesAsync(cts.Token);

            Check($"{parser.Site}: categories loaded", categories.Count > 0, $"got {categories.Count}");
            if (categories.Count == 0) return;

            Console.WriteLine($"  → {categories.Count} categories, first: " +
                              string.Join(", ", categories.Take(8).Select(c => $"{c.Name}[{c.Key}]")));

            // The regression we are fixing: no made-up names, no A–Z leftovers
            Check($"{parser.Site}: no single-letter categories",
                categories.All(c => c.Name.Trim().Length > 1));
            Check($"{parser.Site}: unique keys",
                categories.Select(c => c.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == categories.Count);

            // Browsing the first two categories must return videos
            foreach (var cat in categories.Take(2))
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90));
                var results = await parser.BrowseCategoryAsync(cat.Key, 1, cts.Token);
                Check($"{parser.Site}: browse \"{cat.Name}\" returns videos", results.Count > 0, $"got {results.Count}");
            }
        }
        catch (Exception ex)
        {
            Check($"{parser.Site}: no exception", false, ex.Message);
        }
    }
}

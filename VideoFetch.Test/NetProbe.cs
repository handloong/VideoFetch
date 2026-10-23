using System;
using System.Threading.Tasks;
using VideoFetch.Models;
using VideoFetch.Services;

namespace VideoFetch.Test;

/// <summary>
/// Minimal connectivity probe: prints the HTTP status (or exception) for the
/// page each site's category parser depends on. Run with --probe.
/// </summary>
internal static class NetProbe
{
    public static async Task RunAsync()
    {
        var settings = new SettingsService().Load();
        HttpClientFactory.Configure(settings.ProxyEnabled ? settings.ProxyUrl : string.Empty);
        Console.WriteLine($"=== probe (proxy: {(settings.ProxyEnabled ? settings.ProxyUrl : "none")}) ===");

        var urls = new[]
        {
            "https://91pinse.com/v/",
            "https://91pinse.com/",
            "https://www.xvideos.com/tags",
            "https://www.xnxx.com/",
            "https://www.pornhub.com/categories",
        };

        foreach (var url in urls)
        {
            try
            {
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                using var resp = await HttpClientFactory.GetClient().SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"  {(int)resp.StatusCode}  {url}  ({body.Length} bytes)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERR   {url}  {ex.GetType().Name}: {ex.Message}");
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                    Console.WriteLine($"          ↳ {inner.GetType().Name}: {inner.Message}");
            }
        }
    }
}

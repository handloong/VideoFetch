using System.Net;
using System.Net.Http;
using VideoFetch.Models;

namespace VideoFetch.Services;

/// <summary>
/// Shared HTTP client factory.
///
/// PornHub CDN tokens are session-scoped: the same CookieContainer that
/// loaded the video page must be reused for API calls and downloads.
/// All site parsers and the download engine must use the same instance.
/// </summary>
public static class HttpClientFactory
{
    private static HttpClient? _client;
    private static string _currentProxy = string.Empty;

    public static HttpClient GetClient(string proxyUrl = "")
    {
        if (_client != null && _currentProxy == proxyUrl)
            return _client;

        _client?.Dispose();
        _currentProxy = proxyUrl;

        // UseCookies = true with a shared CookieContainer is critical:
        // PornHub CDN signs tokens against the session cookies.
        var cookieContainer = new CookieContainer();

        // Pre-seed age-confirmation cookies for PornHub
        foreach (var domain in new[] { "pornhub.com", "cn.pornhub.com", "ev.phncdn.com", "hv-h.phncdn.com" })
        {
            cookieContainer.Add(new Uri($"https://{domain}"), new Cookie("ageconfirmed", "1"));
            cookieContainer.Add(new Uri($"https://{domain}"), new Cookie("age_verified", "1"));
            cookieContainer.Add(new Uri($"https://{domain}"), new Cookie("platform", "pc"));
        }

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = true,
            CookieContainer = cookieContainer,
        };

        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = new WebProxy(proxyUrl, true);
            handler.UseProxy = true;
        }

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        _client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        _client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

        return _client;
    }

    public static void Refresh(string proxyUrl = "")
    {
        _client?.Dispose();
        _client = null;
        _currentProxy = string.Empty;
        GetClient(proxyUrl);
    }
}

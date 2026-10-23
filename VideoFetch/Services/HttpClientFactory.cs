using System.Net;
using System.Net.Http;
using VideoFetch.Models;

namespace VideoFetch.Services;

/// <summary>
/// Shared HTTP client factory.
///
/// PornHub CDN tokens are session-scoped: the same CookieContainer that
/// loaded the video page must be reused for API calls and downloads.
/// All site parsers and the download engine must use the same handler.
///
/// Two clients share one handler:
/// - GetClient():          page/API requests, 60s timeout
/// - GetDownloadClient():  file/segment downloads, no timeout
///   (HttpClient.Timeout covers the whole body read, so a 60s timeout
///   would abort any download longer than 60 seconds)
/// </summary>
public static class HttpClientFactory
{
    private static readonly object _lock = new();
    private static HttpClient? _pageClient;
    private static HttpClient? _downloadClient;
    private static HttpClientHandler? _handler;
    private static string _currentProxy = string.Empty;
    private static string _currentCookieStamp = string.Empty;

    /// <summary>
    /// Client for page/API requests (60s timeout).
    /// </summary>
    public static HttpClient GetClient()
    {
        EnsureConfigured();
        return _pageClient!;
    }

    /// <summary>
    /// Client for file downloads — infinite timeout (cancellation is done
    /// via CancellationToken, not HttpClient.Timeout).
    /// </summary>
    public static HttpClient GetDownloadClient()
    {
        EnsureConfigured();
        return _downloadClient!;
    }

    /// <summary>
    /// Apply proxy settings. Call at startup and whenever settings change.
    /// Rebuilds the handler if the proxy actually changed. The old clients are
    /// NOT disposed: in-flight downloads would crash with ObjectDisposedException;
    /// they get collected by GC once no longer referenced.
    /// </summary>
    /// <param name="siteCookies">
    /// 用户导入的 Cookie（站点 → "a=1; b=2"）。给了就挂到对应域名下，
    /// 用于解锁需要登录态的高清/会员内容。
    /// </param>
    public static void Configure(string proxyUrl, IReadOnlyDictionary<string, string>? siteCookies = null)
    {
        var proxy = proxyUrl?.Trim() ?? string.Empty;
        var cookies = siteCookies ?? new Dictionary<string, string>();

        // Cookie 变了也要重建 handler
        var stamp = proxy + "|" + string.Join("|", cookies
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + kv.Value));

        if (_handler != null && _currentCookieStamp == stamp) return;

        lock (_lock)
        {
            if (_handler != null && _currentCookieStamp == stamp) return;
            _currentProxy = proxy;
            _currentCookieStamp = stamp;
            _handler = CreateHandler(proxy, cookies);
            _pageClient = CreatePageClient(_handler);
            _downloadClient = CreateDownloadClient(_handler);
        }
    }

    private static void EnsureConfigured()
    {
        if (_handler != null) return;
        lock (_lock)
        {
            if (_handler != null) return;
            _handler = CreateHandler(_currentProxy, new Dictionary<string, string>());
            _pageClient = CreatePageClient(_handler);
            _downloadClient = CreateDownloadClient(_handler);
        }
    }

    private static HttpClientHandler CreateHandler(string proxy, IReadOnlyDictionary<string, string> siteCookies)
    {
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

        // 用户导入的 Cookie（覆盖同名预置项，比如自己的 age_verified/pc 值）
        ApplyImportedCookies(cookieContainer, siteCookies);

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = true,
            CookieContainer = cookieContainer,
        };

        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy, true);
            handler.UseProxy = true;
        }

        return handler;
    }

    /// <summary>把导入的 Cookie 挂到各站点主域名（含常见子域）下。</summary>
    private static void ApplyImportedCookies(CookieContainer container, IReadOnlyDictionary<string, string> siteCookies)
    {
        foreach (var (siteName, raw) in siteCookies)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!Enum.TryParse<SiteType>(siteName, ignoreCase: true, out var site)) continue;
            if (!SiteCookies.Domains.TryGetValue(site, out var domain)) continue;

            var pairs = SiteCookies.Parse(raw);
            if (pairs.Count == 0) continue;

            foreach (var host in HostsFor(domain))
            {
                foreach (var (name, value) in pairs)
                {
                    try { container.Add(new Uri($"https://{host}"), new Cookie(name, value)); }
                    catch { /* 非法 cookie 名/值直接跳过 */ }
                }
            }
        }
    }

    /// <summary>域名本身 + 常见子域（这些站点的登录态常在 www 下）。</summary>
    private static IEnumerable<string> HostsFor(string domain)
    {
        yield return domain;
        yield return "www." + domain;

        if (domain == "pornhub.com") yield return "cn.pornhub.com";
        if (domain == "xvideos.com") yield return "www.xvideos.com";
        if (domain == "xnxx.com") yield return "www.xnxx.com";
    }

    private static HttpClient CreatePageClient(HttpClientHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        ApplyDefaultHeaders(client);
        return client;
    }

    private static HttpClient CreateDownloadClient(HttpClientHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        ApplyDefaultHeaders(client);
        return client;
    }

    private static void ApplyDefaultHeaders(HttpClient client)
    {
        // Headers are identical across rebuilds; Clear() guards against duplicates.
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    }
}

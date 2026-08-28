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
    public static void Configure(string proxyUrl)
    {
        var proxy = proxyUrl?.Trim() ?? string.Empty;
        if (_handler != null && _currentProxy == proxy) return;

        lock (_lock)
        {
            if (_handler != null && _currentProxy == proxy) return;
            _currentProxy = proxy;
            _handler = CreateHandler(proxy);
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
            _handler = CreateHandler(_currentProxy);
            _pageClient = CreatePageClient(_handler);
            _downloadClient = CreateDownloadClient(_handler);
        }
    }

    private static HttpClientHandler CreateHandler(string proxy)
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

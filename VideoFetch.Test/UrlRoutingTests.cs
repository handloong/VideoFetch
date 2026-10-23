using System;
using VideoFetch.Models;
using VideoFetch.Services;
using VideoFetch.Services.Sites;

namespace VideoFetch.Test;

/// <summary>
/// URL 路由测试：把一个链接交给正确的站点解析器。
///
/// 原来用 <c>url.Contains("xvideos.com")</c> 判断，既认不出 xvideos.es 镜像，
/// 又会被 "https://evil.com/?u=pornhub.com" 这种链接骗到。
/// </summary>
internal static class UrlRoutingTests
{
    private static int _failed;

    public static bool Run()
    {
        Console.WriteLine("\n[url-routing]");
        var before = _failed;

        Safe("canhandle", CanHandle);
        Safe("service", ServiceRouting);

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

    private static void CanHandle()
    {
        var porn = new PornHubParser();
        var xv = new XVideosParser();
        var xn = new XNxxParser();
        var pinse = new PinSeParser();

        Check("routing: PornHub 主域", porn.CanHandle("https://www.pornhub.com/view_video.php?viewkey=x"));
        Check("routing: PornHub 中国站", porn.CanHandle("https://cn.pornhub.com/view_video.php?viewkey=x"));
        Check("routing: PornHub 镜像是 pornhub.org", porn.CanHandle("https://pornhub.org/view_video.php?viewkey=x"));

        Check("routing: XVideos 主域", xv.CanHandle("https://www.xvideos.com/video.abc/x"));
        Check("routing: XVideos 镜像 .es", xv.CanHandle("https://www.xvideos.es/video.abc/x"));
        Check("routing: XNxx 镜像 .es", xn.CanHandle("https://www.xnxx.es/video-abc/x"));
        Check("routing: 91pinse", pinse.CanHandle("https://91pinse.com/v/470704"));

        // 关键回归：域名只出现在查询串/路径里，不算
        Check("routing: 不在域名里出现就不算",
            !porn.CanHandle("https://evil.com/?u=pornhub.com")
            && !xv.CanHandle("https://evil.com/xvideos.com/video.1")
            && !xn.CanHandle("https://evil.com/xnxx.com")
            && !pinse.CanHandle("https://evil.com/91pinse.com"));

        // 相似域名不能被吞
        Check("routing: 相似域名不误判",
            !porn.CanHandle("https://notpornhub.com/x")
            && !xv.CanHandle("https://myxvideos.com/x"));

        Check("routing: 空值/垃圾值安全",
            !porn.CanHandle("") && !porn.CanHandle(null!) && !porn.CanHandle("不是链接"));
    }

    private static void ServiceRouting()
    {
        var service = new VideoInfoService();

        Check("routing: IsSupported 认正常链接",
            service.IsSupported("https://www.xvideos.com/video.abc/x")
            && service.IsSupported("https://91pinse.com/v/1"));

        Check("routing: IsSupported 拒绝无关链接",
            !service.IsSupported("https://example.com/video/1"));

        Check("routing: DetectSite 正确区分站点",
            service.DetectSite("https://www.pornhub.com/view_video.php?viewkey=x") == SiteType.PornHub
            && service.DetectSite("https://www.xvideos.es/video.abc/x") == SiteType.XVideos
            && service.DetectSite("https://www.xnxx.com/video-abc/x") == SiteType.XNxx
            && service.DetectSite("https://91pinse.com/v/470704") == SiteType.PinSe);
    }

    private static void Check(string name, bool condition, string? actual = null)
    {
        Console.WriteLine(condition
            ? $"  PASS  {name}"
            : $"  FAIL  {name}" + (actual is null ? "" : $"  (实际: {actual})"));
        if (!condition) _failed++;
    }
}

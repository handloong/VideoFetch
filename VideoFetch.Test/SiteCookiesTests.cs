using System;
using System.Linq;
using VideoFetch.Models;
using VideoFetch.Services;

namespace VideoFetch.Test;

/// <summary>
/// Cookie 导入的离线测试。
///
/// 这些站点的高清/会员内容需要登录态（游客只能拿到低码率），所以允许用户
/// 把浏览器里的 document.cookie 粘进来。这里的解析必须宽容：用户会连
/// "Cookie: " 前缀、多余的分号、带 "=" 的值一起复制进来。
/// </summary>
internal static class SiteCookiesTests
{
    private static int _failed;

    public static bool Run()
    {
        Console.WriteLine("\n[site-cookies]");
        var before = _failed;

        Safe("parse", Parse);
        Safe("domains", Domains);

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

    private static void Parse()
    {
        var basic = SiteCookies.Parse("a=1; b=2");
        Check("cookie: 解析两条", basic.Count == 2, basic.Count.ToString());
        Check("cookie: 名称和值", basic[0] == ("a", "1") && basic[1] == ("b", "2"));

        var withPrefix = SiteCookies.Parse("Cookie: session=abc; uid=42");
        Check("cookie: 去掉 'Cookie:' 前缀",
            withPrefix.Count == 2 && withPrefix[0].Name == "session" && withPrefix[0].Value == "abc");

        Check("cookie: null 返回空", SiteCookies.Parse(null).Count == 0);
        Check("cookie: 空字符串返回空", SiteCookies.Parse("   ").Count == 0);

        // 用户常把各种垃圾一起复制进来
        var messy = SiteCookies.Parse(";; platform=pc ;  ; broken ;=noname; ok=1");
        Check("cookie: 忽略空段/无等号/空名称",
            messy.Count == 2 && messy[0].Name == "platform" && messy[1].Name == "ok",
            string.Join(",", messy.Select(p => p.Name)));

        // 值里可能带 "="（base64、签名）
        var withEquals = SiteCookies.Parse("token=ab==; other=x");
        Check("cookie: 值里的 = 保留",
            withEquals[0].Value == "ab==", withEquals[0].Value);

        // 前后空格
        var spaced = SiteCookies.Parse("  k = v ;  k2=v2  ");
        Check("cookie: 名称和值都 trim",
            spaced[0] == ("k", "v") && spaced[1] == ("k2", "v2"),
            $"{spaced[0]}, {spaced[1]}");

        Check("cookie: IsUsable", SiteCookies.IsUsable("a=1") && !SiteCookies.IsUsable(""));
    }

    private static void Domains()
    {
        Check("cookie: 四个站点都有域名映射",
            SiteCookies.SupportedSites.All(s => SiteCookies.Domains.ContainsKey(s)),
            string.Join(",", SiteCookies.SupportedSites));

        Check("cookie: 域名正确",
            SiteCookies.Domains[SiteType.PornHub] == "pornhub.com"
            && SiteCookies.Domains[SiteType.XVideos] == "xvideos.com"
            && SiteCookies.Domains[SiteType.XNxx] == "xnxx.com"
            && SiteCookies.Domains[SiteType.PinSe] == "91pinse.com");
    }

    private static void Check(string name, bool condition, string? actual = null)
    {
        Console.WriteLine(condition
            ? $"  PASS  {name}"
            : $"  FAIL  {name}" + (actual is null ? "" : $"  (实际: {actual})"));
        if (!condition) _failed++;
    }
}

using VideoFetch.Models;

namespace VideoFetch.Services;

/// <summary>
/// 把用户粘贴的 Cookie 字符串解析成键值对，并映射到站点域名。
///
/// 用法场景：这些站点的高清/会员内容需要登录态，游客只能拿到低码率。
/// 用户在浏览器里复制 document.cookie 粘进来即可。
/// </summary>
public static class SiteCookies
{
    /// <summary>站点 → 主域名（Cookie 会被挂到这个域名下）。</summary>
    public static readonly IReadOnlyDictionary<SiteType, string> Domains = new Dictionary<SiteType, string>
    {
        [SiteType.PornHub] = "pornhub.com",
        [SiteType.XVideos] = "xvideos.com",
        [SiteType.XNxx] = "xnxx.com",
        [SiteType.PinSe] = "91pinse.com",
    };

    /// <summary>支持导入 Cookie 的站点（顺序即界面下拉框顺序）。</summary>
    public static readonly IReadOnlyList<SiteType> SupportedSites =
        new[] { SiteType.PinSe, SiteType.XVideos, SiteType.XNxx, SiteType.PornHub };

    /// <summary>
    /// "a=1; b=2" → [(a,1), (b,2)]。
    /// 容错：忽略空段、没有等号的段、以及名称为空的段；值里的 "=" 保留。
    /// </summary>
    public static List<(string Name, string Value)> Parse(string? raw)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        // 有些用户会把整行 "Cookie: a=1; b=2" 一起复制进来
        var text = raw.Trim();
        if (text.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            text = text["cookie:".Length..];

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var piece = part.Trim();
            if (piece.Length == 0) continue;

            var eq = piece.IndexOf('=');
            if (eq <= 0) continue;                 // 没有 "=" 或名称为空

            var name = piece[..eq].Trim();
            var value = piece[(eq + 1)..].Trim();
            if (name.Length == 0) continue;

            result.Add((name, value));
        }

        return result;
    }

    /// <summary>判断这段文本是否至少含一条可用的 Cookie。</summary>
    public static bool IsUsable(string? raw) => Parse(raw).Count > 0;
}

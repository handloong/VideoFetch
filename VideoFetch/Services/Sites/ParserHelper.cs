using HtmlAgilityPack;

namespace VideoFetch.Services.Sites;

/// <summary>
/// Shared helpers for HTML-based site parsers.
/// </summary>
public static class ParserHelper
{
    /// <summary>
    /// Extract a thumbnail URL from an img node. Sites lazy-load thumbnails
    /// into data-src/data-cover, so those must win over the (often placeholder)
    /// src attribute. GetAttributeValue returns "" (not null) when the attribute
    /// is missing, so an ?? chain alone cannot express this fallback.
    /// </summary>
    public static string GetThumbUrl(HtmlNode? img)
    {
        if (img == null) return "";
        foreach (var attr in new[] { "data-src", "data-cover", "src" })
        {
            var v = img.GetAttributeValue(attr, "").Trim();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return "";
    }

    /// <summary>
    /// 解码 HTML 实体，直到结果稳定。
    ///
    /// 站点会把标题二次转义（页面里是 <c>&amp;comma;</c>），只解码一遍会留下
    /// 字面量 <c>&amp;comma;</c> 直接显示在界面上。循环解码到不再变化即可；
    /// 普通文本（"AT&amp;T"、"Tom &amp; Jerry"）解码一次后就稳定，不会误伤。
    /// </summary>
    public static string DecodeEntities(string? text, int maxPasses = 3)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var current = text;
        for (var pass = 0; pass < maxPasses; pass++)
        {
            // 三道解码：WebUtility 管数值实体和常见命名实体，HtmlAgilityPack
            // 自带一张更全的命名实体表，最后用自建表补 HTML5 的标点实体。
            var decoded = HtmlEntity.DeEntitize(System.Net.WebUtility.HtmlDecode(current));
            decoded = ExpandHtml5Entities(decoded);
            if (decoded == current) break;      // 已经稳定
            current = decoded;
        }
        return current;
    }

    private static readonly System.Text.RegularExpressions.Regex NamedEntityPattern = new(
        @"&([a-zA-Z][a-zA-Z0-9]{1,31});",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// HTML5 的 ASCII 标点实体（&amp;comma; &amp;period; …）。.NET 只认 HTML4
    /// 那批，而站点标题转义标点时正好用的就是这套，不补就会把 "&amp;comma;"
    /// 当成正文显示出来。只查表、不猜测：表里没有的 &amp;xxx; 原样保留。
    /// </summary>
    private static readonly Dictionary<string, string> Html5Entities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["comma"] = ",", ["period"] = ".", ["colon"] = ":", ["semi"] = ";", ["excl"] = "!",
        ["quest"] = "?", ["num"] = "#", ["dollar"] = "$", ["percnt"] = "%", ["ast"] = "*",
        ["plus"] = "+", ["equals"] = "=", ["bsol"] = "\\", ["sol"] = "/", ["lowbar"] = "_",
        ["grave"] = "`", ["Hat"] = "^", ["tilde"] = "~", ["commat"] = "@", ["vert"] = "|",
        ["lpar"] = "(", ["rpar"] = ")", ["lsqb"] = "[", ["rsqb"] = "]", ["lcub"] = "{", ["rcub"] = "}",
        ["apos"] = "'", ["nbsp"] = "\u00A0",
        ["lsquo"] = "\u2018", ["rsquo"] = "\u2019", ["ldquo"] = "\u201C", ["rdquo"] = "\u201D",
        ["ndash"] = "\u2013", ["mdash"] = "\u2014", ["hellip"] = "\u2026", ["hyphen"] = "\u2010",
        ["laquo"] = "\u00AB", ["raquo"] = "\u00BB", ["times"] = "\u00D7", ["divide"] = "\u00F7",
        ["deg"] = "\u00B0", ["plusmn"] = "\u00B1", ["permil"] = "\u2030", ["middot"] = "\u00B7",
        ["bull"] = "\u2022",
    };

    private static string ExpandHtml5Entities(string text)
    {
        if (text.IndexOf('&') < 0) return text;

        return NamedEntityPattern.Replace(text, m =>
            Html5Entities.TryGetValue(m.Groups[1].Value, out var replacement) ? replacement : m.Value);
    }
}

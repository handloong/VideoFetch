using HtmlAgilityPack;
using Newtonsoft.Json.Linq;

namespace VideoFetch.Services.Sites;

/// <summary>
/// Shared helpers for HTML-based site parsers.
/// </summary>
public static class ParserHelper
{
    /// <summary>
    /// 把站内相对链接拼成绝对地址。
    ///
    /// 以前三个站点都写 <c>href.StartsWith("http") ? href : baseUrl + href</c>，
    /// 漏了两种情况：<c>//host/path</c>（协议相对）会变成 "https://site.com//host/path"、
    /// <c>video-abc/x</c>（无前导斜杠）会变成 "https://site.comvideo-abc/x"（host 被污染）。
    /// </summary>
    public static string AbsoluteUrl(string baseUrl, string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return "";

        var trimmed = href.Trim();

        // 已经是绝对地址（http/https/其它 scheme）
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) &&
            !trimmed.StartsWith("//", StringComparison.Ordinal))
            return absolute.ToString();

        // 协议相对：//cdn.site/x
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return "https:" + trimmed;

        if (Uri.TryCreate(new Uri(baseUrl), trimmed, out var joined))
            return joined.ToString();

        return baseUrl.TrimEnd('/') + "/" + trimmed.TrimStart('/');
    }

    /// <summary>
    /// 解码 JSON 风格转义（\uXXXX、\\、\"），但**不**使用 Regex.Unescape。
    ///
    /// Regex.Unescape 按正则字面量反转义：标题里的 "\q" 会让它抛
    /// ArgumentException，"\b" 会变成退格控制字符 —— 一个怪字符就能毁掉整页解析。
    /// </summary>
    public static string DecodeJsonEscapes(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (!text.Contains('\\')) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                sb.Append(text[i]);
                continue;
            }

            var next = text[i + 1];
            if (next == 'u' && i + 5 < text.Length &&
                int.TryParse(text.AsSpan(i + 2, 4), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var code))
            {
                sb.Append((char)code);
                i += 5;
                continue;
            }

            switch (next)
            {
                case '\\': sb.Append('\\'); i++; break;
                case '"': sb.Append('"'); i++; break;
                case 'n': sb.Append('\n'); i++; break;
                case 't': sb.Append('\t'); i++; break;
                case 'r': sb.Append('\r'); i++; break;
                // 其它转义（\q 之类）原样保留，绝不抛异常
                default: sb.Append(text[i]); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 读 JSON 标量。quality 字段可能是数组（[]）或对象，直接 ToString() 会得到
    /// "[]"，最后画质 key 变成 mp4_[]、下拉框显示 "[]p"。
    /// </summary>
    public static string ReadScalar(JToken? token) => token switch
    {
        JValue { Value: not null } v => v.ToString(),
        JArray { Count: > 0 } arr => ReadScalar(arr[0]),
        _ => ""
    };

    /// <summary>
    /// 取属性值，属性不存在时退回节点的 InnerText。
    ///
    /// 为什么不能写 <c>node.GetAttributeValue("content", "") ?? node.InnerText</c>：
    /// GetAttributeValue 在属性缺失时返回的是**默认值**（这里是 ""），永远不是 null，
    /// 所以 <c>??</c> 右边是死代码。详情页的 &lt;title&gt; 和列表里的
    /// &lt;span class="duration"&gt; 都没有 content 属性，标题/时长会直接变成空串。
    /// </summary>
    public static string GetAttrOrInner(HtmlNode? node, string attribute)
    {
        if (node == null) return "";
        var value = node.GetAttributeValue(attribute, "").Trim();
        return value.Length > 0 ? value : (node.InnerText ?? "").Trim();
    }

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
    /// URL 是否属于给定域名（含其子域）。
    ///
    /// 不能用 <c>url.Contains("xvideos.com")</c>：那种写法既认不出 xvideos.es
    /// 之类的镜像，又会被 <c>https://evil.com/?u=pornhub.com</c> 这种链接骗到。
    /// </summary>
    public static bool UrlMatches(string? url, params string[] domains)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;

        foreach (var domain in domains)
        {
            if (uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)) return true;
            if (uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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

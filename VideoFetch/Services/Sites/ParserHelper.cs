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
}

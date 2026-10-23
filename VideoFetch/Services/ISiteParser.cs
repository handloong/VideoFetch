using VideoFetch.Models;
using System.Threading.Tasks;

namespace VideoFetch.Services;

/// <summary>
/// Base interface for all site parsers
/// </summary>
public interface ISiteParser
{
    /// <summary>
    /// Site type this parser handles
    /// </summary>
    SiteType Site { get; }

    /// <summary>
    /// Whether this parser can handle the given URL
    /// </summary>
    bool CanHandle(string url);

    /// <summary>
    /// Fetch video metadata and available quality streams
    /// </summary>
    Task<VideoInfo?> FetchVideoInfoAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Search videos by keyword query on this site.
    /// Returns a list of search results (title, url, thumbnail, author, duration).
    /// </summary>
    Task<List<SearchResult>> SearchAsync(string query, int page = 1, CancellationToken ct = default);

    /// <summary>
    /// 该站点是否支持按时长筛选。默认不支持 —— 界面据此决定要不要显示时长下拉框。
    /// </summary>
    bool SupportsDurationFilter => false;

    /// <summary>
    /// 带时长筛选的搜索。默认忽略筛选；支持该能力的站点（目前只有 91pinse）重写它。
    /// </summary>
    Task<List<SearchResult>> SearchAsync(
        string query, int page, DurationFilter duration, CancellationToken ct = default)
        => SearchAsync(query, page, ct);

    /// <summary>
    /// List the site's video categories/tags.
    /// Default implementation returns an empty list (site has no category support).
    /// </summary>
    Task<List<CategoryItem>> GetCategoriesAsync(CancellationToken ct = default)
        => Task.FromResult(new List<CategoryItem>());

    /// <summary>
    /// Browse videos of a category returned by GetCategoriesAsync (its Key).
    /// Default implementation falls back to keyword search using the key.
    /// </summary>
    Task<List<SearchResult>> BrowseCategoryAsync(string categoryKey, int page = 1, CancellationToken ct = default)
        => SearchAsync(categoryKey, page, ct);

    /// <summary>
    /// 带时长筛选的分类浏览。默认忽略筛选。
    /// </summary>
    Task<List<SearchResult>> BrowseCategoryAsync(
        string categoryKey, int page, DurationFilter duration, CancellationToken ct = default)
        => BrowseCategoryAsync(categoryKey, page, ct);
}

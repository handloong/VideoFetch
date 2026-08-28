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
}

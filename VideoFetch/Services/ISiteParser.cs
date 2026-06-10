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
}

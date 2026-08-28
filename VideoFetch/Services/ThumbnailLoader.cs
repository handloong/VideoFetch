using System.IO;
using System.Windows.Media.Imaging;

namespace VideoFetch.Services;

/// <summary>
/// Loads small list thumbnails off the UI thread and returns frozen bitmaps
/// safe to assign from any thread. Used by search/category result lists.
/// </summary>
public static class ThumbnailLoader
{
    private const int DecodeWidth = 160;

    /// <summary>
    /// Download and decode a thumbnail. Returns null on any failure —
    /// the UI keeps showing the placeholder.
    /// </summary>
    public static async Task<BitmapSource?> LoadAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var client = HttpClientFactory.GetClient();
            var bytes = await client.GetByteArrayAsync(Normalize(url));
            using var ms = new MemoryStream(bytes);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // release the stream immediately
            bmp.DecodePixelWidth = DecodeWidth;           // small list thumbnails, low memory
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();                                 // cross-thread safe
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static string Normalize(string url)
        => url.StartsWith("//") ? "https:" + url : url;
}

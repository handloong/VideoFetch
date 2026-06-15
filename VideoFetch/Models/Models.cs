namespace VideoFetch.Models;

/// <summary>
/// Represents a supported video website
/// </summary>
public enum SiteType
{
    Unknown,
    PornHub,
    XVideos,
    XNxx,
    PinSe
}

/// <summary>
/// Represents a video quality option
/// </summary>
public record QualityOption(string Label, string Value);

/// <summary>
/// Core video information fetched from the site
/// </summary>
public class VideoInfo
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string ViewCount { get; set; } = string.Empty;
    public SiteType Site { get; set; } = SiteType.Unknown;
    public List<QualityOption> Qualities { get; set; } = new();
    /// <summary>
    /// M3U8 master playlist URL or direct download URL, keyed by quality label
    /// </summary>
    public Dictionary<string, string> StreamUrls { get; set; } = new();
}

/// <summary>
/// A download task entry shown in the queue list
/// </summary>
public class DownloadItem : System.ComponentModel.INotifyPropertyChanged
{
    private double _progress;
    private string _status = "Waiting";
    private string _speed = string.Empty;
    private bool _isCompleted;
    private bool _hasFailed;

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public SiteType Site { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(nameof(Progress)); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); }
    }

    public string Speed
    {
        get => _speed;
        set { _speed = value; OnPropertyChanged(nameof(Speed)); }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set { _isCompleted = value; OnPropertyChanged(nameof(IsCompleted)); }
    }

    public bool HasFailed
    {
        get => _hasFailed;
        set { _hasFailed = value; OnPropertyChanged(nameof(HasFailed)); }
    }

    public CancellationTokenSource CancellationSource { get; set; } = new();

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>
/// Application settings persisted to disk
/// </summary>
public class AppSettings
{
    public string OutputDirectory { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoFetch");
    public string ProxyUrl { get; set; } = string.Empty;
    public int MaxConcurrentDownloads { get; set; } = 2;
    public int MaxSegmentThreads { get; set; } = 8;
    public string DefaultQuality { get; set; } = "best";
    public bool AutoOpenFolder { get; set; } = false;
    public bool CreateSubfolders { get; set; } = false;
    public bool UseBuiltInPlayer { get; set; } = false;
}

/// <summary>
/// Represents a single search result item
/// </summary>
public class SearchResult : System.ComponentModel.INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _url = string.Empty;
    private string _author = string.Empty;
    private string _duration = string.Empty;
    private string _thumbnailUrl = string.Empty;
    private SiteType _site;
    private bool _isSelected;

    public string Title { get => _title; set { _title = value; OnPropertyChanged(nameof(Title)); } }
    public string Url { get => _url; set { _url = value; OnPropertyChanged(nameof(Url)); } }
    public string Author { get => _author; set { _author = value; OnPropertyChanged(nameof(Author)); } }
    public string Duration { get => _duration; set { _duration = value; OnPropertyChanged(nameof(Duration)); } }
    public string ThumbnailUrl { get => _thumbnailUrl; set { _thumbnailUrl = value; OnPropertyChanged(nameof(ThumbnailUrl)); } }
    public SiteType Site { get => _site; set { _site = value; OnPropertyChanged(nameof(Site)); } }

    /// <summary>
    /// Whether this result is selected in the batch checkbox list
    /// </summary>
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }

    /// <summary>
    /// 1-based index for display in the ListView (set by ViewModel)
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Whether this video has been downloaded before (set by ViewModel from DB).
    /// Used for visual indicator (green tint) and duplicate prevention.
    /// </summary>
    public bool IsAlreadyDownloaded { get; set; }

    /// <summary>
    /// Local file path — set when IsAlreadyDownloaded is true.
    /// Used for double-click to play local file with default player.
    /// </summary>
    public string LocalFilePath { get; set; } = string.Empty;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>
/// Represents a category/tag from a supported site
/// </summary>
public class CategoryItem
{
    public string Name { get; set; } = string.Empty;
    public SiteType Site { get; set; }
    public int VideoCount { get; set; }

    public override string ToString() => Name;
}

/// <summary>
/// Download record stored in SQLite database.
/// Used to prevent duplicate downloads and track download history.
/// Based on DeepGrab reference: record is only written when download completes.
/// Similar to Xunlei/Thunder dedup logic:
/// - Record stored by URL (unique key) only after file is saved
/// - Before download: check if record exists AND local file exists
/// - If record exists but file deleted → allow re-download
/// </summary>
public class DownloadRecord
{
    /// <summary>Primary key</summary>
    public int Id { get; set; }

    /// <summary>Video page URL (UNIQUE - used for dedup)</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Video title at time of download</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Source site name</summary>
    public string Site { get; set; } = string.Empty;

    /// <summary>Selected quality label</summary>
    public string Quality { get; set; } = string.Empty;

    /// <summary>Absolute path to saved video file</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>File size in bytes</summary>
    public long FileSize { get; set; }

    /// <summary>When download completed (ISO datetime string from DB)</summary>
    public DateTime DownloadedAt { get; set; }

    /// <summary>Whether the local file still exists on disk</summary>
    public bool FileExists => !string.IsNullOrEmpty(FilePath) && System.IO.File.Exists(FilePath);
}

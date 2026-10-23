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
/// Download page category filter (Xunlei-style)
/// </summary>
public enum QueueFilter
{
    All,
    Downloading,
    Completed,
    Failed,
    Deleted
}

/// <summary>
/// 下载任务的机器状态。界面文案按这个枚举本地化，取色也只看它 ——
/// 不要再用字符串比较（以前靠 StartsWith("Failed") 判断）。
/// </summary>
public enum DownloadState
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Cancelled,
    Deleted,
    FileMissing
}

/// <summary>
/// 按时长筛选视频（对应 91pinse 的 ?dur= 参数）。
/// 只有声明了 <c>SupportsDurationFilter</c> 的站点会真正使用它。
/// </summary>
public enum DurationFilter
{
    All,
    /// <summary>&lt; 5 分钟</summary>
    Short,
    /// <summary>5-20 分钟</summary>
    Medium,
    /// <summary>20+ 分钟</summary>
    Long
}

/// <summary>
/// 时长下拉框的显示项（文案本地化，Value 用于请求）。
/// ToString 返回 Label：ComboBox 收起时用 SelectionBoxItem 渲染，
/// 没有它就会把整条记录的 ToString 显示出来（"DurationOption { Label = … }"）。
/// </summary>
public record DurationOption(string Label, DurationFilter Value)
{
    public override string ToString() => Label;
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
    private string _speed = string.Empty;
    private bool _isCompleted;
    private bool _hasFailed;
    private bool _isActive;
    private bool _isDeleted;
    private bool _isFileMissing;
    private bool _isCancelled;
    private DownloadState _state = DownloadState.Queued;
    private string _lastError = string.Empty;
    private string _note = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public SiteType Site { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Task is still active (queued or downloading). Set false on
    /// completion, failure or cancellation. History entries are never active.
    /// Queue clearing and dedup must rely on this instead of parsing Status strings.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPropertyChanged(nameof(IsActive)); NotifyRowActions(); }
    }

    /// <summary>
    /// True once the download actually started (semaphore acquired);
    /// false while still waiting in the queue.
    /// </summary>
    public bool IsDownloading { get; set; }

    /// <summary>
    /// 软删除：任务被移入回收站。文件可能仍在磁盘上（保留去重记录）。
    /// </summary>
    public bool IsDeleted
    {
        get => _isDeleted;
        set { _isDeleted = value; OnPropertyChanged(nameof(IsDeleted)); NotifyRowActions(); }
    }

    /// <summary>
    /// 本地文件已不存在（下载前被删、通过应用删除或历史文件丢失）
    /// —— 与软删除一样归入"已删除"视图。
    /// </summary>
    public bool IsFileMissing
    {
        get => _isFileMissing;
        set { _isFileMissing = value; OnPropertyChanged(nameof(IsFileMissing)); NotifyRowActions(); }
    }

    /// <summary>
    /// 用户主动取消（或排队中就被取消）的任务。与失败区分开：取消不算错误，
    /// 但要和失败一样提供“重试”入口，否则任务点了取消就再也起不来。
    /// </summary>
    public bool IsCancelled
    {
        get => _isCancelled;
        set { _isCancelled = value; OnPropertyChanged(nameof(IsCancelled)); NotifyRowActions(); }
    }

    /// <summary>
    /// 回收站成员（软删除或文件丢失），用于切换行内按钮组（恢复/彻底删除）。
    /// </summary>
    public bool IsInTrash => !IsActive && (IsDeleted || IsFileMissing);

    // ── 行内操作按钮的可见性（由状态推导，避免界面里堆多重转换器） ──

    /// <summary>取消：仅排队中/下载中。</summary>
    public bool CanCancel => IsActive;

    /// <summary>重试：已结束且没成功（失败或取消）。</summary>
    public bool CanRetry => !IsActive && !IsCompleted && (HasFailed || IsCancelled);

    /// <summary>删除任务：任何已结束、且还没进回收站的任务（失败/取消也必须有删除入口）。</summary>
    public bool CanDelete => !IsActive && !IsInTrash;

    /// <summary>删除本地文件：仅已完成、文件确实在磁盘上、且不在回收站里。</summary>
    public bool CanDeleteFile => !IsActive && IsCompleted && !IsFileMissing && !IsInTrash;

    /// <summary>
    /// 恢复：软删除的任务，且恢复出来还能用。已完成但文件已经没了的任务
    /// 恢复回去也只是个坏任务，界面只提供“彻底删除”，避免点了没反应。
    /// </summary>
    public bool CanRestore => IsDeleted && !(IsCompleted && IsFileMissing);

    /// <summary>进回收站前的状态，恢复时用来还原（例如失败原因）。</summary>
    public DownloadState? StateBeforeTrash { get; set; }

    /// <summary>
    /// 状态位变化后刷新所有由状态推导出来的属性。逐个通知太多容易漏，
    /// 这里统一发一轮，属性很少、开销可以忽略。
    /// </summary>
    private void NotifyRowActions()
    {
        OnPropertyChanged(nameof(IsInTrash));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanDeleteFile));
        OnPropertyChanged(nameof(CanRestore));
    }

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(nameof(Progress)); }
    }

    /// <summary>
    /// 机器状态：逻辑判断与取色都看它。
    /// </summary>
    public DownloadState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(Status));
        }
    }

    /// <summary>失败原因（仅 <see cref="DownloadState.Failed"/> 时有意义）。</summary>
    public string LastError
    {
        get => _lastError;
        set
        {
            _lastError = value ?? string.Empty;
            OnPropertyChanged(nameof(LastError));
            OnPropertyChanged(nameof(Status));
        }
    }

    /// <summary>
    /// 临时展示文案，覆盖状态文本（例如"删除失败: 文件被占用"）。
    /// 只影响显示，不参与任何逻辑判断。
    /// </summary>
    public string Note
    {
        get => _note;
        set
        {
            _note = value ?? string.Empty;
            OnPropertyChanged(nameof(Note));
            OnPropertyChanged(nameof(Status));
        }
    }

    /// <summary>
    /// 界面上显示的状态文本：本地化的状态名（失败时带上原因）。
    /// 中英文切换后重新求值即可，不需要谁去改字符串。
    /// </summary>
    public string Status
    {
        get
        {
            if (_note.Length > 0) return _note;

            var text = Services.LanguageService.GetString(StateResourceKey(_state));
            return _state == DownloadState.Failed && _lastError.Length > 0
                ? $"{text}: {_lastError}"
                : text;
        }
    }

    private static string StateResourceKey(DownloadState state) => state switch
    {
        DownloadState.Queued => "State_Queued",
        DownloadState.Downloading => "State_Downloading",
        DownloadState.Completed => "State_Completed",
        DownloadState.Failed => "State_Failed",
        DownloadState.Cancelled => "State_Cancelled",
        DownloadState.Deleted => "State_Deleted",
        DownloadState.FileMissing => "State_FileMissing",
        _ => "State_Queued"
    };

    /// <summary>状态变化时让界面重新求值一次状态文本（语言切换后也一样）。</summary>
    public void RefreshStatusText() => OnPropertyChanged(nameof(Status));

    public string Speed
    {
        get => _speed;
        set { _speed = value; OnPropertyChanged(nameof(Speed)); }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        set { _isCompleted = value; OnPropertyChanged(nameof(IsCompleted)); NotifyRowActions(); }
    }

    public bool HasFailed
    {
        get => _hasFailed;
        set { _hasFailed = value; OnPropertyChanged(nameof(HasFailed)); NotifyRowActions(); }
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
    /// <summary>Proxy switch — the address is kept in settings even when disabled.</summary>
    public bool ProxyEnabled { get; set; } = false;
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

    /// <summary>
    /// Decoded thumbnail (small, frozen) — loaded in the background after the
    /// result is added to a list. Null until loaded; the UI shows a placeholder.
    /// </summary>
    private System.Windows.Media.Imaging.BitmapSource? _thumbnailImage;
    public System.Windows.Media.Imaging.BitmapSource? ThumbnailImage
    {
        get => _thumbnailImage;
        set { _thumbnailImage = value; OnPropertyChanged(nameof(ThumbnailImage)); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>
/// Represents a category/tag from a supported site.
/// Key is the browse key: a category slug ("amateur"), a URL path ("/c/xxx"),
/// or a plain keyword (sites without real category pages browse via keyword search).
/// </summary>
public class CategoryItem
{
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
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

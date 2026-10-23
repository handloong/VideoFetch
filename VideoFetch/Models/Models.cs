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
/// 下载队列的排序列。点表头切换，默认按下载时间倒序（最新的在最上面）。
/// </summary>
public enum QueueSortKey
{
    Title,
    Duration,
    Size,
    Progress,
    Status,
    DownloadTime
}

/// <summary>
/// 结果列表的排序方式。列表本身来自站点的相关度/更新顺序，"默认"就保持原样。
/// </summary>
public enum ResultSort
{
    /// <summary>站点返回的原始顺序</summary>
    Default,
    /// <summary>时长从长到短（未知时长排最后）</summary>
    DurationDesc,
    /// <summary>时长从短到长（未知时长排最后）</summary>
    DurationAsc
}

/// <summary>
/// 排序下拉框的显示项。和 <see cref="DurationOption"/> 一样要重写 ToString，
/// 否则 ComboBox 收起时会显示 "SortOption { Label = … Value = … }"。
/// </summary>
public record SortOption(string Label, ResultSort Value)
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
    private string _duration = string.Empty;
    private DateTime? _completedAt;
    private long _fileSize = -1;

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public SiteType Site { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>文件大小（字节）。未知用 -1 —— 0 字节的文件实际上不存在，
    /// 但老记录里 0 就是"没记"，所以 <= 0 一律当未知。</summary>
    public long FileSize
    {
        get => _fileSize;
        set
        {
            _fileSize = value;
            OnPropertyChanged(nameof(FileSize));
            OnPropertyChanged(nameof(FileSizeText));
        }
    }

    /// <summary>界面显示的文件大小（用最大单位，如 "1.2G" / "12.3M"）。</summary>
    public string FileSizeText => _fileSize <= 0 ? Services.FileSizeFormat.Unknown : Services.FileSizeFormat.Format(_fileSize);

    /// <summary>
    /// 视频时长文案（站点原样，如 "0:02:14" / "11 min" / "13分钟"）。
    /// 入队时有 <see cref="VideoInfo.Duration"/> 就用它，没有就用列表里那一条的。
    /// </summary>
    public string Duration
    {
        get => _duration;
        set
        {
            _duration = value ?? string.Empty;
            DurationSeconds = Services.DurationText.ToSeconds(_duration);
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(DurationText));
        }
    }

    /// <summary>时长秒数，解析不出为 0。</summary>
    public int DurationSeconds { get; private set; }

    /// <summary>界面显示的时长；没有就显示「—」，不要留白让人以为坏了。</summary>
    public string DurationText => string.IsNullOrWhiteSpace(_duration) ? "—" : _duration;

    /// <summary>下载结束（成功）的时刻；还没下完就是 null。</summary>
    public DateTime? CompletedAt
    {
        get => _completedAt;
        set
        {
            _completedAt = value;
            OnPropertyChanged(nameof(CompletedAt));
            OnPropertyChanged(nameof(DownloadTime));
            OnPropertyChanged(nameof(DownloadTimeText));
        }
    }

    /// <summary>
    /// 「下载时间」列：下好了显示完成时刻，还在排队/下载中显示入队时刻。
    /// 历史记录（从 SQLite 读回来的）没有 CompletedAt，AddedAt 就是当时的下载时间。
    /// </summary>
    public string DownloadTimeText => DownloadTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// 「下载时间」列的排序键。队列视图按它倒序排，最新的在最上面。
    /// 历史记录没有 CompletedAt，AddedAt 就是当时的下载时间。
    /// </summary>
    public DateTime DownloadTime => _completedAt ?? AddedAt;

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

    /// <summary>
    /// 内置播放器音量（0–1）。改了音量就记住，下次打开沿用。
    /// </summary>
    public double PlayerVolume { get; set; } = 1.0;

    /// <summary>结果列表的排序偏好（记住上次选择）。</summary>
    public ResultSort ResultSort { get; set; } = ResultSort.Default;

    /// <summary>
    /// 用户导入的 Cookie：站点名（SiteType 的 ToString）→ "a=1; b=2"。
    /// 用于登录态才能拿到的高清/会员内容。
    /// </summary>
    public Dictionary<string, string> SiteCookies { get; set; } = new();
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
    private int _durationSeconds;
    private int _index;
    private string _thumbnailUrl = string.Empty;
    private SiteType _site;
    private bool _isSelected;

    public string Title { get => _title; set { _title = value; OnPropertyChanged(nameof(Title)); } }
    public string Url { get => _url; set { _url = value; OnPropertyChanged(nameof(Url)); } }
    public string Author { get => _author; set { _author = value; OnPropertyChanged(nameof(Author)); } }
    public string Duration
    {
        get => _duration;
        set
        {
            _duration = value;
            // 排序只认秒数：站点文案格式各不相同（10:52 / 11 min / 0:02:14），
            // 在赋值处统一算一次，四个解析器就不用各自记住这件事。
            DurationSeconds = Services.DurationText.ToSeconds(value);
            OnPropertyChanged(nameof(Duration));
        }
    }

    /// <summary>
    /// 时长的秒数，解析不出就是 0。<see cref="Duration"/> 一变就重算，
    /// 所以任何解析器只要填了文案就自动具备排序能力。
    /// </summary>
    public int DurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            if (_durationSeconds == value) return;
            _durationSeconds = value;
            OnPropertyChanged(nameof(DurationSeconds));
        }
    }
    public string ThumbnailUrl { get => _thumbnailUrl; set { _thumbnailUrl = value; OnPropertyChanged(nameof(ThumbnailUrl)); } }
    public SiteType Site { get => _site; set { _site = value; OnPropertyChanged(nameof(Site)); } }

    /// <summary>
    /// Whether this result is selected in the batch checkbox list
    /// </summary>
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }

    /// <summary>
    /// 1-based index for display in the ListView (set by ViewModel)
    /// </summary>
    // 必须发通知：按序号重排（如按时长排序）后这个值会变，
    // 不通知界面就会留着旧序号（"最长"那行还挂着 1，看起来像没排）。
    public int Index
    {
        get => _index;
        set
        {
            if (_index == value) return;
            _index = value;
            OnPropertyChanged(nameof(Index));
        }
    }

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

    /// <summary>视频时长文案（站点原样），队列里「时长」列用</summary>
    public string Duration { get; set; } = string.Empty;

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

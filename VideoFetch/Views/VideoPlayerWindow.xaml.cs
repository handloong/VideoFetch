using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VideoFetch.Models;
using VideoFetch.Services;

namespace VideoFetch.Views;

public partial class VideoPlayerWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _volumeSaveTimer;

    /// <summary>
    /// 设置对象的引用（与主窗口共用同一个实例），音量变化直接写回去。
    /// 为 null 时表示"没人管持久化"（例如单独 new 一个窗口），此时绝不落盘，
    /// 免得拿一份默认设置把用户的配置文件覆盖掉。
    /// </summary>
    private AppSettings? _settings;

    private bool _isSeeking;
    private bool _isMediaUpdating;

    /// <summary>已经请求播放，但还在等窗口加载 / 媒体打开。</summary>
    private bool _pendingPlay;

    /// <summary>
    /// 播放/暂停图标由模板 DataTrigger 绑定此属性驱动，
    /// 不再用代码改模板元素（窗口未显示时模板还没应用，找不到元素）。
    /// </summary>
    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(VideoPlayerWindow),
            new PropertyMetadata(false));

    public VideoPlayerWindow() : this(null) { }

    /// <param name="settings">
    /// 主窗口的设置实例；传入后音量会读写 <see cref="AppSettings.PlayerVolume"/>。
    /// </param>
    public VideoPlayerWindow(AppSettings? settings)
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdateTimeDisplay();

        _volumeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _volumeSaveTimer.Tick += (_, _) => { _volumeSaveTimer.Stop(); PersistVolume(); };

        // 恢复上次的音量。这一步必须在拿到 _settings 之前做 ——
        // 否则赋值会触发 ValueChanged，白白写一次盘。
        var volume = Math.Clamp(settings?.PlayerVolume ?? 1.0, 0, 1);
        Media.Volume = volume;
        VolumeSlider.Value = volume * 100;
        _settings = settings;

        // 进度条 seek：按下 → 标记拖动；拖动 → 实时刷新时间标签；松开 → 提交跳转。
        // 不能依赖 Thumb.DragStarted/DragCompleted：IsMoveToPointEnabled 下点击轨道拖动
        // 不触发 Thumb 事件，定时器会把滑条值拽回播放位置（表现为"拖不动"）。
        TimeSlider.PreviewMouseLeftButtonDown += (_, _) => _isSeeking = true;
        TimeSlider.PreviewMouseLeftButtonUp += (_, _) => CommitSeek();
        TimeSlider.LostMouseCapture += (_, _) => CommitSeek();
        TimeSlider.ValueChanged += TimeSlider_ValueChanged;

        // 窗口真正显示出来以后才可能开始播（MediaElement 不在视觉树里时 Play 会丢）
        Loaded += (_, _) => { _timer.Start(); StartPlaybackIfReady(); };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _volumeSaveTimer.Stop();
            PersistVolume();
            Media.Close();
        };
    }

    /// <summary>
    /// 打开并播放一个文件。
    ///
    /// 注意：这里**不能**直接调 Media.Play()。调用方是
    /// <c>new VideoPlayerWindow(); player.Play(path); player.Show();</c>，
    /// 此窗口还没加载，MediaElement 不在视觉树里，Play 命令会被丢弃 ——
    /// 表现就是"画面停在第一帧，拖一下进度条才开始播"。
    /// 因此只登记播放请求，等窗口 Loaded 之后再真正开播。
    /// </summary>
    public void Play(string filePath)
    {
        Title = "VideoFetch Player — " + Path.GetFileName(filePath);

        _pendingPlay = true;
        Media.Source = new Uri(filePath);

        StartPlaybackIfReady();
    }

    /// <summary>
    /// 窗口显示出来之后才真正开播。
    ///
    /// 这里**不能**等 MediaOpened：LoadedBehavior="Manual" 的 MediaElement 不会
    /// 自己打开媒体，正是 Play() 去触发打开 —— 等它先来就成了死锁。
    /// 但也不能在窗口加载前调用（命令会被丢弃，表现就是"停在第一帧"）。
    /// </summary>
    private void StartPlaybackIfReady()
    {
        if (!_pendingPlay || !IsLoaded) return;

        _pendingPlay = false;
        Media.Play();
        IsPlaying = true;
    }

    /// <summary>
    /// 媒体已就绪（MediaOpened）：填进度条总长度，并兜底再试一次开播
    /// （正常情况下 Loaded 时已经播上了）。
    /// </summary>
    public void NotifyMediaOpened()
    {
        if (Media.NaturalDuration.HasTimeSpan)
            TimeSlider.Maximum = Media.NaturalDuration.TimeSpan.TotalMilliseconds;

        StartPlaybackIfReady();
    }

    private void TogglePlay()
    {
        if (Media.Source == null) return;
        if (IsPlaying)
        {
            Media.Pause();
            IsPlaying = false;
        }
        else
        {
            Media.Play();
            IsPlaying = true;
        }
    }

    private void Media_MediaOpened(object sender, RoutedEventArgs e) => NotifyMediaOpened();

    private void Media_MediaEnded(object sender, RoutedEventArgs e)
    {
        IsPlaying = false;
        Media.Stop();
    }

    private void Media_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        IsPlaying = false;
        _pendingPlay = false;
        TimeLabel.Text = "播放失败";
    }

    private void UpdateTimeDisplay()
    {
        if (_isSeeking || !Media.NaturalDuration.HasTimeSpan) return;

        var pos = Media.Position;
        var len = Media.NaturalDuration.TimeSpan;
        TimeLabel.Text = $@"{pos:mm\:ss} / {len:mm\:ss}";
        _isMediaUpdating = true;
        try { TimeSlider.Value = pos.TotalMilliseconds; }
        finally { _isMediaUpdating = false; }
    }

    /// <summary>
    /// 用户拖动进度条（轨道点击或 Thumb 拖拽都会走到这里）：
    /// 拖动期间只刷新时间标签，不与定时器回写打架。
    /// </summary>
    private void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isMediaUpdating) return;
        _isSeeking = true;

        if (Media.Source != null && Media.NaturalDuration.HasTimeSpan)
            TimeLabel.Text = $@"{TimeSpan.FromMilliseconds(e.NewValue):mm\:ss} / {Media.NaturalDuration.TimeSpan:mm\:ss}";
    }

    /// <summary>
    /// 松开鼠标后把滑条位置真正应用到播放器（暂停时也能看到画面跳转，依赖 ScrubbingEnabled）。
    /// </summary>
    private void CommitSeek()
    {
        if (!_isSeeking) return;
        _isSeeking = false;

        if (Media.Source == null || !Media.NaturalDuration.HasTimeSpan) return;
        Media.Position = TimeSpan.FromMilliseconds(TimeSlider.Value);
    }

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        => TogglePlay();

    private void BtnVolumeMute_Click(object sender, RoutedEventArgs e)
    {
        if (Media.Volume > 0) SetVolume(0);
        else SetVolume(1.0);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Media is null) return;
        Media.Volume = VolumeSlider.Value / 100.0;

        // 先把新音量记进设置对象（与主窗口共用），稍后再落盘
        if (_settings is not null)
        {
            _settings.PlayerVolume = Media.Volume;
            ScheduleVolumeSave();
        }
    }

    /// <summary>
    /// 拖音量条会连续触发 ValueChanged（一次拖动几十次），
    /// 用 600ms 防抖合并成一次写盘。
    /// </summary>
    private void ScheduleVolumeSave()
    {
        if (_volumeSaveTimer is null) return;   // InitializeComponent 期间还没建出来
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    private void PersistVolume()
    {
        if (_settings is null) return;
        try { new SettingsService().Save(_settings); }
        catch { /* 音量存不下来不该影响播放 */ }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape or Key.Q: Close(); break;
            case Key.Space: TogglePlay(); e.Handled = true; break;
            case Key.Left: Media.Position -= TimeSpan.FromSeconds(10); break;
            case Key.Right: Media.Position += TimeSpan.FromSeconds(10); break;
            case Key.Up: SetVolume(Media.Volume + 0.1); break;
            case Key.Down: SetVolume(Media.Volume - 0.1); break;
            case Key.M: BtnVolumeMute_Click(sender, e); break;
        }
    }

    private void SetVolume(double v)
    {
        v = Math.Clamp(v, 0, 1);
        Media.Volume = v;
        VolumeSlider.Value = v * 100;   // 顺带触发 ValueChanged → 记进设置
    }
}

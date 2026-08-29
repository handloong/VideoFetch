using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VideoFetch.Views;

public partial class VideoPlayerWindow : Window
{
    private readonly DispatcherTimer _timer;
    private bool _isSeeking;
    private bool _isMediaUpdating;

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

    public VideoPlayerWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdateTimeDisplay();

        // 进度条 seek：按下 → 标记拖动；拖动 → 实时刷新时间标签；松开 → 提交跳转。
        // 不能依赖 Thumb.DragStarted/DragCompleted：IsMoveToPointEnabled 下点击轨道拖动
        // 不触发 Thumb 事件，定时器会把滑条值拽回播放位置（表现为"拖不动"）。
        TimeSlider.PreviewMouseLeftButtonDown += (_, _) => _isSeeking = true;
        TimeSlider.PreviewMouseLeftButtonUp += (_, _) => CommitSeek();
        TimeSlider.LostMouseCapture += (_, _) => CommitSeek();
        TimeSlider.ValueChanged += TimeSlider_ValueChanged;

        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => { _timer.Stop(); Media.Close(); };
    }

    public void Play(string filePath)
    {
        Title = "VideoFetch Player — " + System.IO.Path.GetFileName(filePath);
        Media.Source = new Uri(filePath);
        Media.Play();
        IsPlaying = true;
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

    private void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        TimeSlider.Maximum = Media.NaturalDuration.TimeSpan.TotalMilliseconds;
    }

    private void Media_MediaEnded(object sender, RoutedEventArgs e)
    {
        IsPlaying = false;
        Media.Stop();
    }

    private void Media_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        IsPlaying = false;
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
        if (Media.Volume > 0) { Media.Volume = 0; VolumeSlider.Value = 0; }
        else { Media.Volume = 1.0; VolumeSlider.Value = 100; }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Media.Volume = VolumeSlider.Value / 100.0;
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
        VolumeSlider.Value = v * 100;
    }
}

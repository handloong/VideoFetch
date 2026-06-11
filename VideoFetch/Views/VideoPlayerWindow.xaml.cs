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
    private bool _isPlaying;

    public VideoPlayerWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdateTimeDisplay();

        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => { _timer.Stop(); Media.Close(); };
    }

    public void Play(string filePath)
    {
        Title = "VideoFetch Player — " + System.IO.Path.GetFileName(filePath);
        Media.Source = new Uri(filePath);
        Media.Play();
        _isPlaying = true;
        SetPlayIcon(false);
    }

    private void SetPlayIcon(bool showPlay)
    {
        var path = (Path)((ControlTemplate)BtnPlayPause.Template).FindName("Icon", BtnPlayPause);
        if (path != null)
            path.Data = showPlay
                ? Geometry.Parse("M 1,1 L 11,6 L 1,11 Z")
                : Geometry.Parse("M 2,1 L 5,1 L 5,11 L 2,11 Z M 8,1 L 11,1 L 11,11 L 8,11 Z");
    }

    private void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        TimeSlider.Maximum = Media.NaturalDuration.TimeSpan.TotalMilliseconds;
    }

    private void Media_MediaEnded(object sender, RoutedEventArgs e)
    {
        _isPlaying = false;
        SetPlayIcon(true);
        Media.Stop();
    }

    private void UpdateTimeDisplay()
    {
        if (_isSeeking || !Media.NaturalDuration.HasTimeSpan) return;

        var pos = Media.Position;
        var len = Media.NaturalDuration.TimeSpan;
        TimeLabel.Text = $@"{pos:mm\:ss} / {len:mm\:ss}";
        TimeSlider.Value = pos.TotalMilliseconds;
    }

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            Media.Pause();
            _isPlaying = false;
            SetPlayIcon(true);
        }
        else
        {
            Media.Play();
            _isPlaying = true;
            SetPlayIcon(false);
        }
    }

    private void BtnVolumeMute_Click(object sender, RoutedEventArgs e)
    {
        if (Media.Volume > 0) { Media.Volume = 0; VolumeSlider.Value = 0; }
        else { Media.Volume = 1.0; VolumeSlider.Value = 100; }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Media.Volume = VolumeSlider.Value / 100.0;
    }

    private void TimeSlider_DragStarted(object sender, RoutedEventArgs e)
    {
        _isSeeking = true;
    }

    private void TimeSlider_DragCompleted(object sender, RoutedEventArgs e)
    {
        Media.Position = TimeSpan.FromMilliseconds(TimeSlider.Value);
        _isSeeking = false;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape or Key.Q: Close(); break;
            case Key.Space: BtnPlayPause_Click(sender, e); e.Handled = true; break;
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

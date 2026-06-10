using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VideoFetch.Models;
using VideoFetch.Services;

namespace VideoFetch.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// Custom chrome: title bar with min/max/close, drag-to-move
/// Uses WindowChrome (NOT AllowsTransparency) to avoid breaking GridView rendering
/// </summary>
public partial class MainWindow : Window
{
    // ──── Image Preview Overlay ──────────────────────────

    /// <summary>
    /// Full-screen overlay for thumbnail preview on double-click.
    /// ESC key closes it.
    /// </summary>
    private Grid? _previewOverlay;
    private Image? _previewImage;

    public MainWindow()
    {
        InitializeComponent();

        // 启动时根据保存的语言偏好加载（在窗口显示前，安全）
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var langFile = System.IO.Path.Combine(appData, "VideoFetch", "language.txt");
        var culture = "en-US";
        if (System.IO.File.Exists(langFile))
        {
            var saved = System.IO.File.ReadAllText(langFile).Trim();
            if (!string.IsNullOrEmpty(saved)) culture = saved;
        }

        // 启动时一次性加载，不会触发 Visual_HasParent
        LanguageService.SwitchLanguage(culture);

        // Register ESC key handler to close preview overlay
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    // ──── Title Bar Drag ────────────────────────────────

    /// <summary>
    /// Allow dragging the window by the custom title bar (logo area + center gap).
    /// Button area (right column) is excluded — clicks on buttons pass through cleanly.
    /// </summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
        {
            DragMove();
        }
    }

    // ──── Window Control Buttons ───────────────────────

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ──── Language Switcher ─────────────────────────────

    /// <summary>
    /// <summary>
    /// Handle language ComboBox selection change.
    /// Restarts the app to avoid Visual_HasParent crash from ResourceDictionary swap.
    /// </summary>
    private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        var culture = item.Tag as string ?? "en-US";
        if (LanguageService.CurrentLanguage == culture) return;

        // 保存语言选择到文件
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var langFile = System.IO.Path.Combine(appData, "VideoFetch", "language.txt");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(langFile)!);
        System.IO.File.WriteAllText(langFile, culture);

        // 重启应用
        var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exePath))
        {
            System.Diagnostics.Process.Start(exePath);
            Application.Current.Shutdown();
        }
    }

    // ──── Search Results List: Double-Click Preview ─────

    /// <summary>
    /// Handle double-click on search results list row:
    /// - If video is already downloaded → play local file with default player
    /// - Otherwise → show full-screen preview of the thumbnail image.
    /// User can press ESC or click background to close preview.
    /// </summary>
    private void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView lv && lv.SelectedItem is SearchResult result)
        {
            // 如果已经下载过且本地文件存在 → 双击用默认播放器打开
            if (result.IsAlreadyDownloaded && !string.IsNullOrWhiteSpace(result.LocalFilePath))
            {
                if (System.IO.File.Exists(result.LocalFilePath))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = result.LocalFilePath,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show(
                            $"无法打开文件: {ex.Message}", "播放失败",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                }
                else
                {
                    // 本地文件不存在，更新状态
                    result.IsAlreadyDownloaded = false;
                    result.LocalFilePath = string.Empty;
                    System.Windows.MessageBox.Show(
                        "本地文件已被删除。", "文件不存在",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
                return;
            }

            // 未下载：显示缩略图预览
            if (string.IsNullOrWhiteSpace(result.ThumbnailUrl)) return;
            ShowImagePreview(result.ThumbnailUrl, result.Title);
        }
    }

    /// <summary>
    /// Double-click on a completed download: open the video file with default player.
    /// </summary>
    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView lv && lv.SelectedItem is DownloadItem item)
        {
            if (item.IsCompleted && !string.IsNullOrWhiteSpace(item.OutputPath))
            {
                if (System.IO.File.Exists(item.OutputPath))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = item.OutputPath,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
            }
        }
    }

    // ── Image Preview Overlay ──────────────────────────

    /// <summary>
    /// Display a semi-transparent overlay with the image centered.
    /// Click anywhere outside the image or press ESC to close.
    /// </summary>
    private void ShowImagePreview(string imageUrl, string title)
    {
        // Remove existing overlay if any
        CloseImagePreview();

        // Create dark semi-transparent backdrop
        _previewOverlay = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            // Cover entire window content area
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = Cursors.Hand,
            // Allow keyboard focus for ESC key handling
            Focusable = true
        };

        // Create image element
        _previewImage = new Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(imageUrl)),
            Stretch = Stretch.Uniform,
            MaxWidth = ActualWidth * 0.75,
            MaxHeight = ActualHeight * 0.80,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Arrow
        };

        // Optional: title text below image
        var titleText = new TextBlock
        {
            Text = title ?? string.Empty,
            Foreground = Brushes.White,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = ActualWidth * 0.70,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
            TextAlignment = TextAlignment.Center
        };

        // Layout container for image + title
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(_previewImage);
        stack.Children.Add(titleText);

        // "Press ESC to close" hint
        var hint = new TextBlock
        {
            Text = "ESC / 点击空白处关闭",
            Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 16, 16)
        };

        _previewOverlay.Children.Add(stack);
        _previewOverlay.Children.Add(hint);

        // Click on backdrop (not on image) closes the preview
        _previewOverlay.MouseLeftButtonDown += (s, ea) =>
        {
            // Only close if click was directly on the backdrop, not on child elements
            if (ea.Source == _previewOverlay || ea.Source == stack || ea.Source == hint)
            {
                CloseImagePreview();
            }
        };

        // Add overlay to the window's root grid (Grid.Row="2" is main content)
        // We need to find the root grid and add overlay as topmost layer
        var rootGrid = this.Content as Grid;
        if (rootGrid != null)
        {
            // Set overlay to span all rows and be the topmost layer
            Grid.SetRowSpan(_previewOverlay, rootGrid.RowDefinitions.Count);
            Grid.SetZIndex(_previewOverlay, 9999);
            rootGrid.Children.Add(_previewOverlay);

            // Focus the overlay so it can receive ESC key
            _previewOverlay.Focus();
        }
    }

    /// <summary>
    /// Close the image preview overlay if it's open.
    /// </summary>
    private void CloseImagePreview()
    {
        if (_previewOverlay != null)
        {
            var rootGrid = this.Content as Grid;
            if (rootGrid != null && rootGrid.Children.Contains(_previewOverlay))
            {
                rootGrid.Children.Remove(_previewOverlay);
            }
            _previewOverlay = null;
            _previewImage = null;
        }
    }

    /// <summary>
    /// Global ESC key handler — closes image preview overlay when visible.
    /// Also allows normal window-level ESC behavior.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _previewOverlay != null)
        {
            CloseImagePreview();
            e.Handled = true; // Prevent other ESC handlers from firing
        }
    }
}

using System.Windows;
using System.Windows.Input;

namespace VideoFetch.Views;

/// <summary>
/// Main window — title bar + toolbar + tab host.
/// Content tabs are separate UserControls under Views/.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var langFile = System.IO.Path.Combine(appData, "VideoFetch", "language.txt");
        var culture = "zh-CN";
        if (System.IO.File.Exists(langFile))
        {
            var saved = System.IO.File.ReadAllText(langFile).Trim();
            if (!string.IsNullOrEmpty(saved)) culture = saved;
        }
        Services.LanguageService.SwitchLanguage(culture);

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) CloseImagePreview(); };
    }

    // ── Title bar drag ──────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
            DragMove();
    }

    // ── Title bar buttons ───────────────────────────────

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => Close();

    // ── Image preview support (used by SearchTab via app) ─

    public void ShowImagePreview(string url, string title)
    {
        CloseImagePreview();
        _previewOverlay = new System.Windows.Controls.Grid
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(200, 0, 0, 0)),
            Focusable = true,
            Cursor = Cursors.Hand
        };
        _previewImage = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new System.Uri(url)),
            Stretch = System.Windows.Media.Stretch.Uniform,
            MaxWidth = ActualWidth * 0.9,
            MaxHeight = ActualHeight * 0.9
        };
        _previewOverlay.Children.Add(_previewImage);
        _previewOverlay.MouseDown += (_, _) => CloseImagePreview();
        _previewOverlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) CloseImagePreview(); };

        var root = Content as System.Windows.Controls.Grid;
        root?.Children.Add(_previewOverlay);
        _previewOverlay.Focus();
    }

    public void CloseImagePreview()
    {
        if (_previewOverlay == null) return;
        var root = Content as System.Windows.Controls.Grid;
        root?.Children.Remove(_previewOverlay);
        _previewOverlay = null;
        _previewImage = null;
    }

    private System.Windows.Controls.Grid? _previewOverlay;
    private System.Windows.Controls.Image? _previewImage;
}

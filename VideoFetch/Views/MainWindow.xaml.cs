using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace VideoFetch.Views;

/// <summary>
/// Main window — title bar + toolbar + tab host.
/// Content tabs are separate UserControls under Views/.
/// </summary>
public partial class MainWindow : Window
{
    // ── Win32 constants for WM_NCHITTEST override ──────
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT    = 1;    // client area — WPF handles click
    private const int HTCAPTION   = 2;    // title bar — OS handles drag/move

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

        // Hook into the window's message loop to fix hit-testing for custom title bar buttons.
        // With WindowStyle=None + WindowChrome, the entire caption area (top 38px) is
        // treated as HTCAPTION, which swallows mouse clicks before they reach our buttons.
        // We intercept WM_NCHITTEST and tell the OS: "if the mouse is over our buttons,
        // treat it as HTCLIENT so WPF handles the click."
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// Install WndProc hook after the window handle (HWND) is created.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    /// <summary>
    /// Intercept WM_NCHITTEST. When the mouse is over our custom minimize/close
    /// buttons, force HTCLIENT so WPF can route the click to the Button element.
    /// Otherwise let the default WindowChrome behavior handle it (drag, resize, etc.).
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST)
            return IntPtr.Zero;

        // Convert screen coordinates to WPF coordinates relative to this window
        var screenX = (int)(lParam.ToInt64() & 0xFFFF);
        var screenY = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
        var wpfPoint = PointFromScreen(new Point(screenX, screenY));

        // Hit-test: is the mouse over our custom buttons?
        var result = VisualTreeHelper.HitTest(this, wpfPoint);
        if (result == null)
            return IntPtr.Zero;  // let OS decide (HTCAPTION for drag, HTxxx for resize)

        // Walk up the visual tree — if we hit a Button inside our TitleBarButtons
        // panel, force HTCLIENT so the click goes through to WPF.
        var element = result.VisualHit as DependencyObject;
        while (element != null)
        {
            if (element == MinimizeBtn || element == CloseBtn)
            {
                handled = true;
                return new IntPtr(HTCLIENT);
            }
            element = VisualTreeHelper.GetParent(element);
        }

        // Not over our buttons — let WindowChrome handle it normally
        return IntPtr.Zero;
    }

    // ── Title bar drag ──────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
            DragMove();
    }

    // ── Title bar buttons ───────────────────────────────

    /// <summary>
    /// Minimize the window. Works because WndProc ensures the button area
    /// returns HTCLIENT so the click actually reaches this handler.
    /// </summary>
    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    /// <summary>
    /// Close the window.
    /// </summary>
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

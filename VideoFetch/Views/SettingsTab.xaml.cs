using System;
using System.Windows;
using System.Windows.Controls;

namespace VideoFetch.Views;

public partial class SettingsTab : UserControl
{
    public SettingsTab() => InitializeComponent();

    private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;
        var culture = item.Tag as string ?? "zh-CN";
        if (Services.LanguageService.CurrentLanguage == culture) return;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var langFile = System.IO.Path.Combine(appData, "VideoFetch", "language.txt");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(langFile)!);
        System.IO.File.WriteAllText(langFile, culture);

        var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exePath))
        {
            System.Diagnostics.Process.Start(exePath);
            Application.Current.Shutdown();
        }
    }
}

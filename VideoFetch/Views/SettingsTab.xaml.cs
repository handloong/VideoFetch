using System;
using System.Windows;
using System.Windows.Controls;

namespace VideoFetch.Views;

public partial class SettingsTab : UserControl
{
    /// <summary>程序化选中语言项会触发 SelectionChanged，用这个标记跳过。</summary>
    private bool _initializing;

    public SettingsTab()
    {
        InitializeComponent();

        // 构造时就选一次：MainWindow 在 InitializeComponent 里创建这些控件，
        // 此时语言可能还没切换完，所以真正显示时（Loaded）还要再对一次。
        SelectCurrentLanguage();
        Loaded += (_, _) => SelectCurrentLanguage();
    }

    private void SelectCurrentLanguage()
    {
        var current = Services.LanguageService.CurrentLanguage;
        _initializing = true;
        try
        {
            foreach (var obj in LangCombo.Items)
            {
                if (obj is ComboBoxItem item && (item.Tag as string) == current)
                {
                    LangCombo.SelectedItem = item;
                    break;
                }
            }
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>
    /// 切换语言：立即生效（DynamicResource 会跟着换字典，ViewModel 里的文案快照由
    /// LanguageChanged 重建），同时写进 language.txt 供下次启动使用。
    /// 以前这里是"写文件 + 重启进程"，但语言资源路径当时是坏的，切了也没反应。
    /// </summary>
    private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not ComboBoxItem item) return;

        var culture = item.Tag as string ?? "zh-CN";
        if (Services.LanguageService.CurrentLanguage == culture) return;

        if (!Services.LanguageService.SwitchLanguage(culture)) return;

        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var langFile = System.IO.Path.Combine(appData, "VideoFetch", "language.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(langFile)!);
            System.IO.File.WriteAllText(langFile, culture);
        }
        catch (Exception ex)
        {
            Services.AppLog.Warn($"写入 language.txt 失败（本次切换仍已生效）: {ex.Message}");
        }
    }
}

using System.Windows.Controls;
using System.Windows.Input;
using VideoFetch.Models;

namespace VideoFetch.Views;

public partial class QueueTab : UserControl
{
    public QueueTab() => InitializeComponent();

    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView lv || lv.SelectedItem is not DownloadItem item) return;
        if (!item.IsCompleted || string.IsNullOrWhiteSpace(item.OutputPath)) return;
        if (!System.IO.File.Exists(item.OutputPath)) return;

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

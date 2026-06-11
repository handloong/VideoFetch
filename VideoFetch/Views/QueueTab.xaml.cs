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

        var vm = DataContext as ViewModels.MainViewModel;
        bool useBuiltIn = vm?.Settings.UseBuiltInPlayer == true;

        if (useBuiltIn)
        {
            var player = new VideoPlayerWindow();
            player.Play(item.OutputPath);
            player.Show();
        }
        else
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

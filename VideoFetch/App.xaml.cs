using System.Windows;
using VideoFetch.Services;

namespace VideoFetch;

public partial class App : System.Windows.Application
{
    private static bool _crashDialogShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 先装兜底，再做别的 —— 启动阶段也可能抛异常
        InstallGlobalExceptionHandlers();

        AppLog.TrimOldFiles();
        AppLog.Info($"启动 VideoFetch {AppVersion()} ({Environment.OSVersion})，日志目录 {AppLog.Directory}");

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"退出，代码 {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    /// <summary>
    /// 三个口子都要堵：UI 线程、后台线程、以及没人 await 的 Task。
    /// 没有它们时，一个解析器 bug 就能让整个程序消失，用户既看不到原因，
    /// 未完成的任务也一起丢了。
    /// </summary>
    private static void InstallGlobalExceptionHandlers()
    {
        // UI 线程：记日志后继续运行，别让用户丢掉整个下载队列
        Current.DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("UI 线程未处理异常（已拦截，程序继续运行）", args.Exception);
            args.Handled = true;
            ShowCrashHintOnce(args.Exception);
        };

        // 后台线程：.NET 里这类异常必然终止进程，只能尽量记下来
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Error("后台线程未处理异常（进程将退出）", args.ExceptionObject as Exception);
            AppLog.TrimOldFiles();
        };

        // 没人 await 的任务：标记已观察，避免进程被终结，同时记录下来
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };
    }

    private static void ShowCrashHintOnce(Exception ex)
    {
        // 同一个坑可能每帧都在抛，只提示第一次，其余静默记日志
        if (_crashDialogShown) return;
        _crashDialogShown = true;

        try
        {
            MessageBox.Show(
                $"出现了一个内部错误，程序已继续运行。\n\n{ex.Message}\n\n" +
                $"详细信息已记录到：\n{AppLog.CurrentFile}",
                "VideoFetch",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // 弹窗失败也不能再抛
        }
    }

    private static string AppVersion()
        => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";
}

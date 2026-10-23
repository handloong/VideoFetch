using System.IO;
using System.Text;

namespace VideoFetch.Services;

/// <summary>
/// 极简崩溃/运行日志。写到 %AppData%\VideoFetch\logs\app-yyyy-MM-dd.log。
///
/// 为什么不用 Serilog/NLog：这里只需要"出事了能查"——单文件追加、线程安全、
/// 异常带堆栈、单条消息截断、旧文件轮转。几十行就够，别加依赖。
/// </summary>
public static class AppLog
{
    private const int MaxMessageChars = 8000;   // 单条消息上限，防止巨型 HTML/JSON 把日志撑爆
    private const int DefaultKeepFiles = 7;

    private static readonly object _lock = new();
    private static string _directory = DefaultDirectory();

    /// <summary>日志目录。测试可以改写它。</summary>
    public static string Directory
    {
        get => _directory;
        set
        {
            lock (_lock)
            {
                _directory = string.IsNullOrWhiteSpace(value) ? DefaultDirectory() : value;
                try { System.IO.Directory.CreateDirectory(_directory); } catch { }
            }
        }
    }

    /// <summary>当前正在写的日志文件（按天切分）。</summary>
    public static string CurrentFile
        => System.IO.Path.Combine(Directory, $"app-{DateTime.Now:yyyy-MM-dd}.log");

    private static string DefaultDirectory()
        => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoFetch", "logs");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>只保留最近 keep 个日志文件（按文件名即日期排序）。</summary>
    public static void TrimOldFiles(int keep = DefaultKeepFiles)
    {
        try
        {
            var files = System.IO.Directory.GetFiles(Directory, "app-*.log");
            if (files.Length <= keep) return;

            foreach (var file in files
                         .OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal)
                         .Skip(keep))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // 清理失败不影响日志功能
        }
    }

    private static void Write(string level, string message, Exception? ex)
    {
        var sb = new StringBuilder(256);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append(" [").Append(level).Append("] ")
          .Append(Truncate(message, MaxMessageChars));

        if (ex != null)
        {
            sb.AppendLine();
            sb.Append("  ").Append(Truncate(ex.ToString(), MaxMessageChars)
                .Replace("\n", "\n  "));
        }

        sb.AppendLine();

        try
        {
            lock (_lock)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(CurrentFile, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去也不能再抛异常，否则把调用方也带崩
        }
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text[..max] + "…(truncated)";
    }
}

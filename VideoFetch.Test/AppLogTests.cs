using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VideoFetch.Services;

namespace VideoFetch.Test;

/// <summary>
/// 崩溃日志的离线测试。
///
/// 之前 App 没有任何全局异常处理和日志：UI 线程一抛异常程序直接消失，
/// 用户既看不到原因，未完成的任务也一起没了。这里保证日志本身可靠：
/// 能落盘、带时间戳和级别、异常带堆栈、并发不串行、超长内容不撑爆磁盘、
/// 旧文件会被清理。
/// </summary>
internal static class AppLogTests
{
    private static int _failed;

    public static bool Run()
    {
        Console.WriteLine("\n[applog]");
        var before = _failed;

        var dir = Path.Combine(Path.GetTempPath(), "vf-log-" + Guid.NewGuid().ToString("N")[..8]);
        var original = AppLog.Directory;
        AppLog.Directory = dir;

        try
        {
            Safe("basic", Basic);
            Safe("exception", WithException);
            Safe("concurrent", Concurrent);
            Safe("truncate", Truncate);
            Safe("cleanup", Cleanup);
        }
        finally
        {
            AppLog.Directory = original;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        return _failed == before;
    }

    private static void Safe(string name, Action test)
    {
        try { test(); }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
    }

    private static string ReadCurrent()
    {
        var file = AppLog.CurrentFile;
        return File.Exists(file) ? File.ReadAllText(file) : "";
    }

    private static void Basic()
    {
        AppLog.Info("hello from test");

        Check("log: 文件已创建", File.Exists(AppLog.CurrentFile));
        Check("log: 目录存在", Directory.Exists(AppLog.Directory));

        var text = ReadCurrent();
        Check("log: 包含消息内容", text.Contains("hello from test"));
        Check("log: 带级别标记", text.Contains("[INFO]"));
        Check("log: 带时间戳", System.Text.RegularExpressions.Regex.IsMatch(
            text, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}"));
        Console.WriteLine($"  info  日志文件: {AppLog.CurrentFile}");
    }

    private static void WithException()
    {
        // 必须真的抛出再捕获，异常才会有堆栈（new 出来的没有）
        Exception ex;
        try { throw new InvalidOperationException("boom-detail"); }
        catch (Exception caught) { ex = caught; }

        AppLog.Error("下载失败", ex);

        var text = ReadCurrent();
        Check("log: 错误级别", text.Contains("[ERROR]"));
        Check("log: 异常消息", text.Contains("boom-detail"));
        Check("log: 异常类型", text.Contains(nameof(InvalidOperationException)));
        Check("log: 带堆栈", text.Contains("at "));
    }

    private static void Concurrent()
    {
        var before = ReadCurrent().Split('\n').Length;
        Parallel.For(0, 50, i => AppLog.Info($"parallel-{i}"));

        var text = ReadCurrent();
        var beforeAfter = text.Split('\n').Length;
        var hits = Enumerable.Range(0, 50).Count(i => text.Contains($"parallel-{i}"));
        Check("log: 并发 50 条全部写入", hits == 50);
        Check("log: 行数增加", beforeAfter >= before + 50);
    }

    private static void Truncate()
    {
        AppLog.Info(new string('x', 200_000));

        var size = new FileInfo(AppLog.CurrentFile).Length;
        Console.WriteLine($"  info  超长消息写入后文件大小: {size / 1024} KB");
        Check("log: 超长消息被截断（文件 < 64KB）", size < 64 * 1024);
    }

    private static void Cleanup()
    {
        // 造一批旧日志文件
        for (var i = 1; i <= 10; i++)
            File.WriteAllText(Path.Combine(AppLog.Directory, $"app-2020-01-{i:00}.log"), "old");

        AppLog.TrimOldFiles(keep: 3);

        var remaining = Directory.GetFiles(AppLog.Directory, "app-*.log");
        Console.WriteLine($"  info  清理后剩余 {remaining.Length} 个日志文件");
        Check("log: 旧日志被清理到 keep 个", remaining.Length <= 3);
        Check("log: 当前日志没被删", File.Exists(AppLog.CurrentFile));
    }

    private static void Check(string name, bool condition)
    {
        Console.WriteLine(condition ? $"  PASS  {name}" : $"  FAIL  {name}");
        if (!condition) _failed++;
    }
}

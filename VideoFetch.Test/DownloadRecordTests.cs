using System;
using System.IO;
using Microsoft.Data.Sqlite;
using VideoFetch.Services;

namespace VideoFetch.Test;

/// <summary>
/// 下载记录（SQLite）的离线测试：时长要能存进去读回来，
/// 而且**已经存在的 downloads.db** 打开时要自动补上 Duration 列 ——
/// CREATE TABLE IF NOT EXISTS 对老库不生效，不补列的话历史记录的时长永远是空。
/// </summary>
internal static class DownloadRecordTests
{
    private static int _failed;
    private static string _dir = string.Empty;

    public static bool Run()
    {
        Console.WriteLine("\n[download-record]");
        var before = _failed;

        _dir = Path.Combine(Path.GetTempPath(), "vf-db-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);

        try
        {
            Safe("roundtrip", RoundTrip);
            Safe("migrate", MigratesOldDatabase);
        }
        finally
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        return _failed == before;
    }

    private static void RoundTrip()
    {
        var db = Path.Combine(_dir, "rt.db");
        var svc = new DownloadRecordService(db);

        svc.InsertRecord("https://site.test/v/1", "带时长的视频", "PinSe", "best",
            @"C:\videos\1.ts", 12345, "0:02:14");

        var rec = svc.GetByUrl("https://site.test/v/1");
        Check("record: 记录写进去了", rec != null);
        if (rec == null) return;

        Check("record: 时长读回来", rec.Duration == "0:02:14", rec.Duration);
        Check("record: 其它字段不受影响",
            rec.Title == "带时长的视频" && rec.FileSize == 12345 && rec.Quality == "best",
            $"{rec.Title}/{rec.FileSize}/{rec.Quality}");

        // 同一 URL 再写一次（换时长）→ INSERT OR REPLACE 覆盖
        svc.InsertRecord("https://site.test/v/1", "带时长的视频", "PinSe", "best",
            @"C:\videos\1.ts", 12345, "10:00");
        Check("record: 重下会覆盖时长",
            svc.GetByUrl("https://site.test/v/1")?.Duration == "10:00",
            svc.GetByUrl("https://site.test/v/1")?.Duration);

        // 不传时长（老调用方式）→ 空字符串，不出错
        svc.InsertRecord("https://site.test/v/2", "没时长", "XVideos", "720p", @"C:\videos\2.ts");
        Check("record: 不传时长也能写",
            svc.GetByUrl("https://site.test/v/2")?.Duration == string.Empty);
    }

    /// <summary>造一个"旧版本"库（没有 Duration 列），确认打开时会补列且不丢数据。</summary>
    private static void MigratesOldDatabase()
    {
        var db = Path.Combine(_dir, "old.db");

        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE DownloadRecords (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Url TEXT UNIQUE NOT NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    Site TEXT NOT NULL DEFAULT '',
                    Quality TEXT NOT NULL DEFAULT '',
                    FilePath TEXT NOT NULL DEFAULT '',
                    FileSize INTEGER NOT NULL DEFAULT 0,
                    DownloadedAt TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO DownloadRecords (Url, Title, Site, Quality, FilePath, FileSize, DownloadedAt)
                VALUES ('https://site.test/old/1', '老记录', 'XVideos', '720p', 'C:\videos\old.ts', 10, '2026-10-01T12:00:00.0000000');
            ";
            cmd.ExecuteNonQuery();
        }

        // 打开就会补列
        var svc = new DownloadRecordService(db);

        var all = svc.GetAllRecords();
        Check("migrate: 老记录还在", all.Count == 1, all.Count.ToString());
        if (all.Count == 0) return;

        Check("migrate: 老记录标题没丢", all[0].Title == "老记录", all[0].Title);
        Check("migrate: 补列后时长为空", all[0].Duration == string.Empty, all[0].Duration);

        // 补列之后新写入的记录必须能带时长
        svc.InsertRecord("https://site.test/old/2", "新记录", "XVideos", "720p",
            @"C:\videos\new.ts", 20, "1:30:00");
        Check("migrate: 补列后能写时长",
            svc.GetByUrl("https://site.test/old/2")?.Duration == "1:30:00",
            svc.GetByUrl("https://site.test/old/2")?.Duration);

        // 再打开一次不应重复 ALTER（重复加同名列会抛异常）
        var again = new DownloadRecordService(db);
        Check("migrate: 重复打开不报错", again.GetAllRecords().Count == 2);
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

    private static void Check(string name, bool condition, string? actual = null)
    {
        Console.WriteLine(condition
            ? $"  PASS  {name}"
            : $"  FAIL  {name}" + (actual is null ? "" : $"  (实际: {actual})"));
        if (!condition) _failed++;
    }
}

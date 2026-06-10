using Microsoft.Data.Sqlite;
using VideoFetch.Models;

namespace VideoFetch.Services;

/// <summary>
/// SQLite download history service (based on DeepGrab reference implementation).
/// 
/// Key design (like Xunlei/Thunder dedup logic):
/// - Record is ONLY inserted when a download completes successfully (file saved to disk).
/// - Before downloading, check GetByUrl(url) → if record exists AND file exists → skip.
/// - If record exists but file was deleted → allow re-download.
/// - Uses INSERT OR REPLACE to handle the rare case of same URL with different quality.
///
/// Database: %APPDATA%/VideoFetch/downloads.db
/// </summary>
public class DownloadRecordService
{
    private readonly string _dbPath;

    public DownloadRecordService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = System.IO.Path.Combine(appData, "VideoFetch");
        if (!System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);

        _dbPath = System.IO.Path.Combine(dir, "downloads.db");

        // 初始化数据库表
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS DownloadRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Url TEXT UNIQUE NOT NULL,
                Title TEXT NOT NULL DEFAULT '',
                Site TEXT NOT NULL DEFAULT '',
                Quality TEXT NOT NULL DEFAULT '',
                FilePath TEXT NOT NULL DEFAULT '',
                FileSize INTEGER NOT NULL DEFAULT 0,
                DownloadedAt TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_dl_url ON DownloadRecords(Url);
        ";
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════
    //  PUBLIC API
    // ══════════════════════════════════════════════

    /// <summary>
    /// Check if a video URL has been successfully downloaded.
    /// This is the dedup check — called BEFORE starting any download.
    /// Also verifies local file still exists (stale record = not counted as downloaded).
    /// </summary>
    /// <param name="url">Video page URL</param>
    /// <returns>True if downloaded AND local file still exists</returns>
    public bool IsDownloaded(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        var record = GetByUrl(url);
        if (record == null) return false;

        // 文件必须还在本地才算"已下载"
        return System.IO.File.Exists(record.FilePath);
    }

    /// <summary>
    /// Get the download record for a URL (null if never downloaded).
    /// </summary>
    public DownloadRecord? GetByUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM DownloadRecords WHERE Url = @u LIMIT 1";
        cmd.Parameters.AddWithValue("@u", url.Trim());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return MapRow(r);
    }

    /// <summary>
    /// Insert or replace a download record when download completes.
    /// Uses INSERT OR REPLACE to handle same URL with different quality/file.
    /// This is the ONLY write method — records are only created when file is actually saved.
    /// </summary>
    /// <param name="url">Video page URL (unique key for dedup)</param>
    /// <param name="title">Video title</param>
    /// <param name="site">Source site name</param>
    /// <param name="quality">Quality label</param>
    /// <param name="filePath">Absolute path to saved video file</param>
    /// <param name="fileSize">File size in bytes</param>
    public void InsertRecord(string url, string title, string site, string quality, string filePath, long fileSize = 0)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // 使用 INSERT OR REPLACE: 同一URL第二次下载 → 覆盖旧记录
        cmd.CommandText = @"
            INSERT OR REPLACE INTO DownloadRecords (Url, Title, Site, Quality, FilePath, FileSize, DownloadedAt)
            VALUES (@u, @t, @s, @q, @p, @z, @d)";
        cmd.Parameters.AddWithValue("@u", url.Trim());
        cmd.Parameters.AddWithValue("@t", title ?? "");
        cmd.Parameters.AddWithValue("@s", site ?? "");
        cmd.Parameters.AddWithValue("@q", quality ?? "");
        cmd.Parameters.AddWithValue("@p", filePath ?? "");
        cmd.Parameters.AddWithValue("@z", fileSize);
        cmd.Parameters.AddWithValue("@d", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Remove a record (for "clear all" or when user wants to re-download).
    /// </summary>
    public void DeleteRecord(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM DownloadRecords WHERE Url = @u";
        cmd.Parameters.AddWithValue("@u", url.Trim());
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get all download records (for history display).
    /// </summary>
    public List<DownloadRecord> GetAllRecords()
    {
        var list = new List<DownloadRecord>();
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM DownloadRecords ORDER BY DownloadedAt DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(MapRow(r));
        return list;
    }

    /// <summary>
    /// Get count of downloaded records.
    /// </summary>
    public int DownloadCount
    {
        get
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM DownloadRecords";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // ══════════════════════════════════════════════
    //  HELPER
    // ══════════════════════════════════════════════

    private static DownloadRecord MapRow(SqliteDataReader r)
    {
        return new DownloadRecord
        {
            Id = r.GetInt32(0),
            Url = r.GetString(1),
            Title = r.GetString(2),
            Site = r.GetString(3),
            Quality = r.GetString(4),
            FilePath = r.GetString(5),
            FileSize = r.GetInt64(6),
            DownloadedAt = DateTime.TryParse(r.GetString(7), out var d) ? d : DateTime.MinValue
        };
    }
}

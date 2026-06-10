using System.IO;
using Newtonsoft.Json;
using VideoFetch.Models;

namespace VideoFetch.Services;

/// <summary>
/// Persists AppSettings to %AppData%\VideoFetch\settings.json
/// </summary>
public class SettingsService
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoFetch");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }
}

/// <summary>
/// SQLite-backed download history service
/// </summary>
public class HistoryService : IDisposable
{
    private readonly string _dbPath;
    private Microsoft.Data.Sqlite.SqliteConnection? _conn;

    public HistoryService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoFetch");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "history.db");
        InitDb();
    }

    private void InitDb()
    {
        _conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS downloads (
                id         TEXT PRIMARY KEY,
                title      TEXT NOT NULL,
                url        TEXT NOT NULL,
                site       TEXT NOT NULL,
                output     TEXT NOT NULL,
                quality    TEXT NOT NULL,
                added_at   TEXT NOT NULL,
                completed  INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void AddEntry(DownloadItem item)
    {
        if (_conn == null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO downloads (id, title, url, site, output, quality, added_at, completed)
            VALUES ($id, $title, $url, $site, $output, $quality, $added_at, $completed)
            """;
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$title", item.Title);
        cmd.Parameters.AddWithValue("$url", item.Url);
        cmd.Parameters.AddWithValue("$site", item.Site.ToString());
        cmd.Parameters.AddWithValue("$output", item.OutputPath);
        cmd.Parameters.AddWithValue("$quality", item.Quality);
        cmd.Parameters.AddWithValue("$added_at", item.AddedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$completed", item.IsCompleted ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public List<HistoryEntry> GetHistory(int limit = 200)
    {
        if (_conn == null) return new();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT title, url, site, output, quality, added_at, completed FROM downloads ORDER BY added_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var result = new List<HistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new HistoryEntry
            {
                Title = reader.GetString(0),
                Url = reader.GetString(1),
                Site = reader.GetString(2),
                OutputPath = reader.GetString(3),
                Quality = reader.GetString(4),
                AddedAt = DateTime.Parse(reader.GetString(5)),
                Completed = reader.GetInt32(6) == 1
            });
        }
        return result;
    }

    public void ClearAll()
    {
        if (_conn == null) return;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM downloads";
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn?.Dispose();
}

public class HistoryEntry
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }
    public bool Completed { get; set; }
}

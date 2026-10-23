using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using VideoFetch.Models;

namespace VideoFetch.Services;

/// <summary>待续任务（写进 queue.json 的那一份，字段刻意保持精简）。</summary>
public class PendingTask
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public SiteType Site { get; set; }
    public string Quality { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DownloadState State { get; set; } = DownloadState.Queued;

    /// <summary>
    /// 上次退出时它还在排队/下载中 —— 下次启动直接接着下。
    /// 失败/取消的任务只恢复显示，不自动重跑（否则每次开机都去撞同一个坏链接）。
    /// </summary>
    public bool WasActive { get; set; }
}

/// <summary>
/// 未完成任务清单。
///
/// 下载记录（SQLite）只存**已完成**的，队列本身是内存对象 —— 关掉程序，
/// 排队中和下载中的任务就没了。这里把它们落盘，启动时再接着下。
///
/// 用独立 JSON 而不是塞进 downloads.db：这份数据是"临时状态"，随时可丢，
/// 丢了最多是任务消失，不该和去重记录共用一张表。
/// </summary>
public class PendingQueueStore
{
    private static string _directory = DefaultDirectory();

    /// <summary>清单所在目录。测试可以改写它，避免写进用户真实配置。</summary>
    public static string Directory
    {
        get => _directory;
        set => _directory = string.IsNullOrWhiteSpace(value) ? DefaultDirectory() : value;
    }

    private static string FilePath => Path.Combine(Directory, "queue.json");

    private static string DefaultDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoFetch");

    /// <summary>读取清单。文件不存在或损坏都返回空列表（宁可丢任务也不要启动失败）。</summary>
    public List<PendingTask> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<PendingTask>();
            var json = File.ReadAllText(FilePath);
            var list = JsonConvert.DeserializeObject<List<PendingTask>>(json);
            return list ?? new List<PendingTask>();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取待续任务清单失败，已忽略: {ex.Message}");
            return new List<PendingTask>();
        }
    }

    /// <summary>
    /// 覆盖写入。只保留"还没完成、也没进回收站"的任务 ——
    /// 完成/删除的那一刻它就该从清单里消失，这样重启后不会幽灵般回来。
    /// </summary>
    public void Save(IEnumerable<DownloadItem> items)
    {
        var pending = items
            .Where(i => !i.IsCompleted && !i.IsDeleted && !i.IsFileMissing)
            .Where(i => !string.IsNullOrWhiteSpace(i.Url))
            .Select(i => new PendingTask
            {
                Url = i.Url,
                Title = i.Title,
                Site = i.Site,
                Quality = i.Quality,
                OutputPath = i.OutputPath,
                Duration = i.Duration,
                AddedAt = i.AddedAt,
                State = i.State,
                WasActive = i.IsActive
            })
            .ToList();

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var json = JsonConvert.SerializeObject(pending, Formatting.Indented);

            // 先写临时文件再替换，避免写到一半断电留下半截 JSON
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入待续任务清单失败: {ex.Message}");
        }
    }
}

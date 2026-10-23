using System;
using System.IO;
using VideoFetch.Models;
using VideoFetch.Services;

namespace VideoFetch.Test;

/// <summary>
/// 配置持久化的离线测试（用临时目录，不碰用户真实配置）。
/// </summary>
internal static class SettingsServiceTests
{
    private static int _failed;

    public static bool Run()
    {
        Console.WriteLine("\n[settings]");
        var before = _failed;

        var dir = Path.Combine(Path.GetTempPath(), "vf-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        var original = SettingsService.Directory;
        SettingsService.Directory = dir;

        try
        {
            Safe("roundtrip", RoundTrip);
            Safe("corrupt", CorruptFile);
            Safe("missing", MissingFile);
            Safe("atomic", NoTempLeftBehind);
        }
        finally
        {
            SettingsService.Directory = original;
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

    private static void RoundTrip()
    {
        var service = new SettingsService();
        var settings = new AppSettings
        {
            OutputDirectory = @"D:\videos",
            MaxConcurrentDownloads = 3,
            ProxyEnabled = true,
            ProxyUrl = "http://127.0.0.1:7890",
            CreateSubfolders = true,
            ResultSort = ResultSort.DurationDesc
        };
        settings.SiteCookies["PinSe"] = "session=abc";

        var ok = service.Save(settings);
        Check("settings: 保存成功", ok);

        var loaded = service.Load();
        Check("settings: 输出目录还原", loaded.OutputDirectory == @"D:\videos", loaded.OutputDirectory);
        Check("settings: 并发数还原", loaded.MaxConcurrentDownloads == 3);
        Check("settings: 代理还原", loaded.ProxyEnabled && loaded.ProxyUrl == "http://127.0.0.1:7890");
        Check("settings: Cookie 还原",
            loaded.SiteCookies.TryGetValue("PinSe", out var ck) && ck == "session=abc");
        Check("settings: 排序偏好还原（枚举要能存进 JSON 再读回来）",
            loaded.ResultSort == ResultSort.DurationDesc, loaded.ResultSort.ToString());
    }

    private static void CorruptFile()
    {
        var path = Path.Combine(SettingsService.Directory, "settings.json");
        File.WriteAllText(path, "{ 这不是合法 JSON ");

        var loaded = new SettingsService().Load();

        Check("settings: 配置损坏时回退到默认值", loaded != null);
        Check("settings: 回退后并发数是默认值", loaded!.MaxConcurrentDownloads == 2, loaded.MaxConcurrentDownloads.ToString());
        Check("settings: 回退后不抛异常", loaded.SiteCookies != null);
    }

    private static void MissingFile()
    {
        var path = Path.Combine(SettingsService.Directory, "settings.json");
        if (File.Exists(path)) File.Delete(path);

        var loaded = new SettingsService().Load();
        Check("settings: 文件不存在时返回默认值", loaded.MaxConcurrentDownloads == 2);
    }

    private static void NoTempLeftBehind()
    {
        // 原子写：临时文件必须被改名掉，不能留在目录里
        new SettingsService().Save(new AppSettings { MaxConcurrentDownloads = 4 });

        var temps = Directory.GetFiles(SettingsService.Directory, "*.tmp");
        Check("settings: 不遗留临时文件", temps.Length == 0, string.Join(",", temps));
    }

    private static void Check(string name, bool condition, string? actual = null)
    {
        Console.WriteLine(condition
            ? $"  PASS  {name}"
            : $"  FAIL  {name}" + (actual is null ? "" : $"  (实际: {actual})"));
        if (!condition) _failed++;
    }
}

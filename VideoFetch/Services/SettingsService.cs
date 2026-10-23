using System.IO;
using Newtonsoft.Json;
using VideoFetch.Models;

namespace VideoFetch.Services;

/// <summary>
/// Persists AppSettings to %AppData%\VideoFetch\settings.json
/// </summary>
public class SettingsService
{
    private static string _directory = DefaultDirectory();

    /// <summary>配置目录。测试可以改写它，避免碰用户真实配置。</summary>
    public static string Directory
    {
        get => _directory;
        set => _directory = string.IsNullOrWhiteSpace(value) ? DefaultDirectory() : value;
    }

    private static string ConfigPath => Path.Combine(Directory, "settings.json");

    private static string DefaultDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoFetch");

    /// <summary>
    /// 读取配置。文件不存在或内容损坏时返回默认设置。
    /// </summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppSettings();

            var json = File.ReadAllText(ConfigPath);
            return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // 以前这里静默吞掉：配置损坏 → 用户所有设置悄悄回到默认值且毫无提示
            // 带上路径：测试会用临时目录故意写坏一份，日志里能一眼看出不是用户的配置
            AppLog.Warn($"读取配置失败，已使用默认设置 ({ConfigPath}): {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>
    /// 保存配置。返回是否成功 —— 界面据此显示"保存成功"还是失败原因。
    ///
    /// 先写临时文件再替换，避免写到一半断电/崩溃把配置弄坏。
    /// </summary>
    public bool Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            var temp = ConfigPath + ".tmp";

            File.WriteAllText(temp, json);
            File.Move(temp, ConfigPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("保存配置失败", ex);
            return false;
        }
    }
}

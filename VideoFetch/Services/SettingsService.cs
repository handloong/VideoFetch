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

using System.Windows;
using System.Linq;

namespace VideoFetch.Services;

/// <summary>
/// Manages application localization (i18n).
/// Switches language at runtime by swapping ResourceDictionary.
/// Default: Chinese (zh-CN)
/// </summary>
public static class LanguageService
{
    private const string ResourcePath = "VideoFetch.Strings.Strings.";

    /// <summary>Currently active culture code, e.g. "zh-CN" or "en-US"</summary>
    public static string CurrentLanguage { get; private set; } = "zh-CN";

    /// <summary>Event fired when language changes</summary>
    public static event Action? LanguageChanged;

    /// <summary>Flag to prevent re-entrant language switches</summary>
    private static bool _isSwitching;

    /// <summary>
    /// Switch to a new language (e.g. "zh-CN", "en-US").
    /// Updates all DynamicResource bindings automatically.
    /// </summary>
    public static bool SwitchLanguage(string cultureCode)
    {
        if (CurrentLanguage == cultureCode) return true;
        if (_isSwitching) return false; // 防止重入导致闪退
        _isSwitching = true;

        try
        {
            // Try loading the new dictionary
            var dictUri = new Uri($"{ResourcePath}{cultureCode}.xaml", UriKind.Relative);
            ResourceDictionary? newDict = null;
            try
            {
                newDict = new ResourceDictionary { Source = dictUri };
            }
            catch (System.IO.FileNotFoundException)
            {
                _isSwitching = false;
                return false;
            }

            // 替换语言字典：先移除旧的再添加新的
            var appResources = Application.Current.Resources;
            var oldDicts = appResources.MergedDictionaries.Where(d =>
                d.Source?.OriginalString?.Contains("Strings.") == true).ToList();

            foreach (var old in oldDicts)
                appResources.MergedDictionaries.Remove(old);

            appResources.MergedDictionaries.Add(newDict);

            CurrentLanguage = cultureCode;
            LanguageChanged?.Invoke();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _isSwitching = false;
        }
    }

    /// <summary>
    /// Get a localized string by key (for use in C# code-behind).
    /// Fallback: returns [key] if not found.
    /// </summary>
    public static string GetString(string key)
    {
        return Application.Current.TryFindResource(key) as string ?? $"[{key}]";
    }

    /// <summary>
    /// Available languages with display names
    /// </summary>
    public static readonly Dictionary<string, string> SupportedLanguages = new()
    {
        ["zh-CN"] = "中文",
        ["en-US"] = "English"
    };

    /// <summary>
    /// List of available language codes
    /// </summary>
    public static IReadOnlyList<string> Languages => SupportedLanguages.Keys.ToList();
}

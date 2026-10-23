using System.Globalization;

namespace VideoFetch.Services;

/// <summary>
/// 文件大小的人类可读格式：永远用**最大**的那个单位，
/// 1234567 → "1.2M"，1288490189 → "1.2G"，512 → "512B"。
///
/// 保留一位小数但不补零（"1G" 而不是 "1.0G"）；用固定区域性格式化，
/// 免得某些语言下变成 "1,2M"。
/// </summary>
public static class FileSizeFormat
{
    private static readonly string[] Units = { "K", "M", "G", "T" };

    /// <summary>未知大小（-1 或 0）返回占位符，不要显示 "0B" 骗人。</summary>
    public const string Unknown = "—";

    public static string Format(long bytes)
    {
        if (bytes <= 0) return Unknown;
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + "B";

        double value = bytes;
        var unit = 0;
        // 阈值取 1023.95 而不是 1024：1048570 字节按 1024 算会停在 "1024K"，
        // 进位到 "1M" 才符合"用最大单位"的初衷。
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1023.95 && unit < Units.Length);

        return value.ToString("0.#", CultureInfo.InvariantCulture) + Units[unit - 1];
    }
}

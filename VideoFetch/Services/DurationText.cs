using System.Text.RegularExpressions;

namespace VideoFetch.Services;

/// <summary>
/// 把各站点五花八门的时长文案转成秒数，供排序/比较使用。
///
/// 四个站点的写法完全不同（都是实测页面里的真实格式）：
///   PornHub   "10:52"            m:ss
///   91PinSe   "0:02:14"          h:mm:ss
///   XVideos   "11 min"
///   XNxx      "11min" / "1sec"   夹在 playback 统计里，和其它文字混排
/// PornHub 详情页还给 ISO 8601 的 "PT1H2M3S"。
///
/// 认不出来一律返回 0（未知）。排序时未知的放最后，绝不因为解析不出就把
/// 一条结果排到最前面。
/// </summary>
public static class DurationText
{
    /// <summary>整串就是时钟格式：h:mm:ss / m:ss。</summary>
    private static readonly Regex ClockPattern = new(
        @"^(?:(\d{1,3}):)?(\d{1,3}):([0-5]\d)$", RegexOptions.Compiled);

    /// <summary>ISO 8601：PT1H2M3S（PornHub 详情页）。</summary>
    private static readonly Regex IsoPattern = new(
        @"^PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 带小时的单位写法，可能跟着分钟："1 h" / "1h 5min" / "1hour 5 minutes"。
    /// 前面的 lookbehind 排除 "6.2M" 这种小数：那里 "2" 前面是 "."，不能被当成 2 分钟。
    /// </summary>
    private static readonly Regex HoursPattern = new(
        @"(?<![\d.\w])(\d+)\s*(?:hours?|hrs?|h)(?:\s*(\d+)\s*(?:minutes?|mins?|m)(?!\w))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MinutesPattern = new(
        @"(?<![\d.\w])(\d+)\s*(?:minutes?|mins?|m)(?!\w)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SecondsPattern = new(
        @"(?<![\d.\w])(\d+)\s*(?:seconds?|secs?|s)(?!\w)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 时长文案 → 秒数。认不出来返回 0。
    /// 输入可以是纯时长（"10:52"），也可以是混排文本（XNxx 的 metadata）。
    /// </summary>
    public static int ToSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        // 中文界面下站点会把时长文案也本地化（XVideos 就是 "13分钟"），
        // 先等价替换成英文单位，后面只维护一套正则。
        var s = Normalize(text.Trim());

        var clock = ClockPattern.Match(s);
        if (clock.Success)
        {
            var hours = clock.Groups[1].Success ? ParseInt(clock.Groups[1].Value) : 0;
            return hours * 3600 + ParseInt(clock.Groups[2].Value) * 60 + ParseInt(clock.Groups[3].Value);
        }

        var iso = IsoPattern.Match(s);
        if (iso.Success && (iso.Groups[1].Success || iso.Groups[2].Success || iso.Groups[3].Success))
        {
            return ParseInt(iso.Groups[1].Value) * 3600
                 + ParseInt(iso.Groups[2].Value) * 60
                 + ParseInt(iso.Groups[3].Value);
        }

        // 带单位的写法，可能是 "1h 5min" 也可能是 "11min" 夹在一堆文字里
        var total = 0;
        var hoursMatch = HoursPattern.Match(s);
        if (hoursMatch.Success)
        {
            total += ParseInt(hoursMatch.Groups[1].Value) * 3600;
            // 小时后面紧跟的分钟已经算在这里，别再让 MinutesPattern 重复加一次
            if (hoursMatch.Groups[2].Success)
                total += ParseInt(hoursMatch.Groups[2].Value) * 60;
        }

        if (!hoursMatch.Success || !hoursMatch.Groups[2].Success)
        {
            var minutesMatch = MinutesPattern.Match(s);
            if (minutesMatch.Success) total += ParseInt(minutesMatch.Groups[1].Value) * 60;
        }

        var secondsMatch = SecondsPattern.Match(s);
        if (secondsMatch.Success) total += ParseInt(secondsMatch.Groups[1].Value);

        return total;
    }

    /// <summary>
    /// 从混排文本里捞出时长片段本身（XNxx 的 metadata 是
    /// " 6.2M 99% 11min - 720p "，直接显示整串太脏）。捞不到返回空串。
    /// </summary>
    public static string Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var s = Normalize(text.Trim());

        var clock = ClockPattern.Match(s);
        if (clock.Success) return clock.Value;

        var hoursMatch = HoursPattern.Match(s);
        if (hoursMatch.Success) return Collapse(hoursMatch.Value);

        var minutesMatch = MinutesPattern.Match(s);
        if (minutesMatch.Success) return Collapse(minutesMatch.Value);

        var secondsMatch = SecondsPattern.Match(s);
        if (secondsMatch.Success) return Collapse(secondsMatch.Value);

        return string.Empty;
    }

    private static int ParseInt(string value)
        => int.TryParse(value, out var n) ? n : 0;

    /// <summary>
    /// 中文单位 → 英文单位。"13分钟" → "13min"、"1小时13分钟" → "1h13min"。
    ///
    /// 站点会根据 Accept-Language 本地化时长文案：同一段 XVideos 结果，
    /// 英文环境给 "11 min"，中文环境给 "11分钟"。不归一化的话中文界面下
    /// 时长全是 0，排序直接失效。
    /// </summary>
    private static string Normalize(string s)
    {
        if (s.IndexOfAny(new[] { '小', '時', '时', '分', '秒' }) < 0) return s;

        return s
            .Replace("小时", " h ")
            .Replace("小時", " h ")
            .Replace("分钟", " min ")
            .Replace("分鐘", " min ")
            .Replace("分", " min ")
            .Replace("秒", " s ");
    }

    /// <summary>"1h   5min" → "1h 5min"（XNxx 的原始文本里有多个空格/换行）。</summary>
    private static string Collapse(string value)
        => Regex.Replace(value, @"\s+", " ").Trim();
}

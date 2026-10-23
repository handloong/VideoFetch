using System;
using System.Linq;
using VideoFetch.Models;
using VideoFetch.Services;

namespace VideoFetch.Test;

/// <summary>
/// 时长解析 + 结果排序的离线测试。
///
/// 时长在四个站点上是四种完全不同的写法（"10:52" / "0:02:14" / "11 min" /
/// "11min"），而 XNxx 还把它跟播放量、清晰度混在同一段文字里 —— "6.2M 99% 11min"
/// 这种文本里，最小的那个数字不是时长。这些用例就是盯着这类坑。
/// </summary>
internal static class DurationSortTests
{
    private static int _failed;

    public static bool Run()
    {
        Console.WriteLine("\n[duration]");
        var before = _failed;

        Safe("formats", Formats);
        Safe("noise", NoiseIsRejected);
        Safe("extract", ExtractToken);
        Safe("model", ModelComputesSeconds);
        Safe("order", Ordering);
        Safe("stability", Stability);
        Safe("inplace", ApplyInPlace);

        return _failed == before;
    }

    // ══════════════════════════════════════════════════════════
    //  各站点的真实写法
    // ══════════════════════════════════════════════════════════

    private static void Formats()
    {
        // PornHub 列表：m:ss
        Equal("10:52 -> 652", 652, DurationText.ToSeconds("10:52"));
        Equal("1:02:03 -> 3723", 3723, DurationText.ToSeconds("1:02:03"));

        // 91pinse：h:mm:ss（小时位可能是 0）
        Equal("0:02:14 -> 134", 134, DurationText.ToSeconds("0:02:14"));
        Equal("2:00:00 -> 7200", 7200, DurationText.ToSeconds("2:00:00"));

        // XVideos：带空格
        Equal("11 min -> 660", 660, DurationText.ToSeconds("11 min"));
        Equal("3 min -> 180", 180, DurationText.ToSeconds("3 min"));

        // XNxx：不带空格，秒级也出现过
        Equal("11min -> 660", 660, DurationText.ToSeconds("11min"));
        Equal("1sec -> 1", 1, DurationText.ToSeconds("1sec"));

        // 多小时
        Equal("1h 5min -> 3900", 3900, DurationText.ToSeconds("1h 5min"));
        Equal("1 h 5 min -> 3900", 3900, DurationText.ToSeconds("1 h 5 min"));
        Equal("2 hours 30 minutes -> 9000", 9000, DurationText.ToSeconds("2 hours 30 minutes"));

        // PornHub 详情页：ISO 8601
        Equal("PT1H2M3S -> 3723", 3723, DurationText.ToSeconds("PT1H2M3S"));
        Equal("PT10M -> 600", 600, DurationText.ToSeconds("PT10M"));
        Equal("PT45S -> 45", 45, DurationText.ToSeconds("PT45S"));

        // 前后空白
        Equal("trimmed", 660, DurationText.ToSeconds("  11 min  "));

        // 中文界面下站点会本地化时长文案（XVideos 实测就是 "13分钟"）
        Equal("13分钟 -> 780", 780, DurationText.ToSeconds("13分钟"));
        Equal("13 分钟（带空格）", 780, DurationText.ToSeconds("13 分钟"));
        Equal("1小时13分钟 -> 4380", 4380, DurationText.ToSeconds("1小时13分钟"));
        Equal("1小时13分钟（带空格）", 4380, DurationText.ToSeconds("1 小时 13 分钟"));
        Equal("1分30秒 -> 90", 90, DurationText.ToSeconds("1分30秒"));
        Equal("繁体 分鐘", 780, DurationText.ToSeconds("13分鐘"));
        Equal("中文 Extract", "13 min", DurationText.Extract("13分钟"));
    }

    private static void NoiseIsRejected()
    {
        Equal("null -> 0", 0, DurationText.ToSeconds(null));
        Equal("空串 -> 0", 0, DurationText.ToSeconds(""));
        Equal("空白 -> 0", 0, DurationText.ToSeconds("   "));
        Equal("纯文字 -> 0", 0, DurationText.ToSeconds("HD"));
        Equal("分辨率不是时长 -> 0", 0, DurationText.ToSeconds("720p"));
        Equal("PT 但没有数字 -> 0", 0, DurationText.ToSeconds("PT"));

        // 关键陷阱：播放量 "6.2M" 里的 2 不能被当成 2 分钟
        Equal("播放量里的数字不算时长", 0, DurationText.ToSeconds("6.2M 99%"));
        Equal("混排文本按真实时长算", 660, DurationText.ToSeconds(" 6.2M 99% 11min - 720p "));
        Equal("混排文本（47min）", 2820, DurationText.ToSeconds("3.3M 99% 47min - 1080p"));

        // 负数/畸形不应崩
        Equal("畸形 -> 0", 0, DurationText.ToSeconds("--:--"));
    }

    private static void ExtractToken()
    {
        // XNxx 的 metadata 里只想要 "11min"，不是整段文字
        Equal("xnxx metadata", "11min", DurationText.Extract(" 6.2M 99% 11min - 720p "));
        Equal("pinse 纯时长原样返回", "0:02:14", DurationText.Extract("0:02:14"));
        Equal("多小时合并", "1h 5min", DurationText.Extract(" 1h  5min "));
        Equal("认不出返回空", "", DurationText.Extract("720p"));
        Equal("null 返回空", "", DurationText.Extract(null));
    }

    private static void ModelComputesSeconds()
    {
        var item = new SearchResult { Duration = "11 min" };
        Equal("Duration 赋值即算出秒数", 660, item.DurationSeconds);

        item.Duration = "0:02:14";
        Equal("改文案会重算", 134, item.DurationSeconds);

        item.Duration = "";
        Equal("清空后归零", 0, item.DurationSeconds);
    }

    // ══════════════════════════════════════════════════════════
    //  排序
    // ══════════════════════════════════════════════════════════

    private static void Ordering()
    {
        var items = new[]
        {
            Item("A", "5 min"),      // 300
            Item("B", ""),           // 未知
            Item("C", "1:30:00"),    // 5400
            Item("D", "45 min"),     // 2700
        };

        var desc = ResultSorting.Order(items, ResultSort.DurationDesc).Select(r => r.Title).ToArray();
        Equal("长→短", "C,D,A,B", string.Join(",", desc));

        var asc = ResultSorting.Order(items, ResultSort.DurationAsc).Select(r => r.Title).ToArray();
        Equal("短→长（未知仍最后）", "A,D,C,B", string.Join(",", asc));

        var none = ResultSorting.Order(items, ResultSort.Default).Select(r => r.Title).ToArray();
        Equal("默认顺序不变", "A,B,C,D", string.Join(",", none));

        // 全是未知时长时不应崩溃，顺序保持原样
        var unknown = new[] { Item("X", ""), Item("Y", "n/a") };
        Equal("全未知保持原序",
            "X,Y",
            string.Join(",", ResultSorting.Order(unknown, ResultSort.DurationDesc).Select(r => r.Title)));
    }

    private static void Stability()
    {
        // 时长相同的两条，必须保持站点原始顺序（LINQ OrderBy 是稳定排序）
        var items = new[]
        {
            Item("first", "10 min"),
            Item("second", "10:00"),
            Item("third", "10min"),
        };

        var desc = ResultSorting.Order(items, ResultSort.DurationDesc).Select(r => r.Title).ToArray();
        Equal("等长不换位", "first,second,third", string.Join(",", desc));
    }

    private static void ApplyInPlace()
    {
        // 通过 IList 接口传入普通 List，覆盖非 ObservableCollection 分支
        var list = new System.Collections.Generic.List<SearchResult>
        {
            Item("A", "5 min"),
            Item("B", "1:30:00"),
        };

        ResultSorting.ApplyInPlace(list, ResultSort.DurationDesc);
        Equal("原地重排", "B,A", string.Join(",", list.Select(r => r.Title)));

        // Default 不做任何移动
        var untouched = new System.Collections.Generic.List<SearchResult>
        {
            Item("A", "5 min"),
            Item("B", "1:30:00"),
        };
        ResultSorting.ApplyInPlace(untouched, ResultSort.Default);
        Equal("Default 不动", "A,B", string.Join(",", untouched.Select(r => r.Title)));
    }

    // ── helpers ──────────────────────────────────────────────

    private static SearchResult Item(string title, string duration)
        => new() { Title = title, Url = "https://site.test/v/" + title, Duration = duration };

    private static void Safe(string name, Action test)
    {
        try { test(); }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}: 抛异常 {ex.GetType().Name}: {ex.Message}");
            _failed++;
        }
    }

    private static void Equal(string name, object expected, object actual)
    {
        var ok = Equals(expected?.ToString(), actual?.ToString());
        Console.WriteLine(ok
            ? $"  PASS  {name}"
            : $"  FAIL  {name}  (期望 {expected}，实际 {actual})");
        if (!ok) _failed++;
    }
}

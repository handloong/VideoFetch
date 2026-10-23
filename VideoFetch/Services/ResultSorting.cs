using VideoFetch.Models;

namespace VideoFetch.Services;

/// <summary>
/// 结果列表排序。抽成纯函数是为了能离线测试：ViewModel 那边只负责把
/// 排好的顺序套回 ObservableCollection。
/// </summary>
public static class ResultSorting
{
    /// <summary>
    /// 按 <paramref name="sort"/> 排出一份新的顺序。
    ///
    /// 两个方向都把"未知时长"（0）放最后 —— 解析不出来的结果不该因为
    /// 短→长而顶到第一屏。
    /// LINQ 的 OrderBy 是稳定排序，所以时长相同的保持站点原始顺序。
    /// </summary>
    public static List<SearchResult> Order(IEnumerable<SearchResult> items, ResultSort sort)
    {
        var list = items as IList<SearchResult> ?? items.ToList();

        return sort switch
        {
            ResultSort.DurationDesc => list
                .OrderBy(r => r.DurationSeconds <= 0 ? 1 : 0)
                .ThenByDescending(r => r.DurationSeconds)
                .ToList(),

            ResultSort.DurationAsc => list
                .OrderBy(r => r.DurationSeconds <= 0 ? 1 : 0)
                .ThenBy(r => r.DurationSeconds)
                .ToList(),

            _ => list.ToList()
        };
    }

    /// <summary>
    /// 把排好的顺序套回一个可变列表，用 Move 而不是 Clear+Add：
    /// 后者会丢掉选中项、滚动位置也会跳回顶部。
    /// </summary>
    public static void ApplyInPlace(IList<SearchResult> target, ResultSort sort)
    {
        if (sort == ResultSort.Default || target.Count < 2) return;

        var ordered = Order(target, sort);

        // target 可能是 ObservableCollection，它实现的是非泛型 IList；
        // 这里只做 Move 需要的定位，具体移动交给调用方。
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = target.IndexOf(ordered[i]);
            if (current < 0 || current == i) continue;
            Move(target, current, i);
        }
    }

    private static void Move(IList<SearchResult> list, int from, int to)
    {
        if (list is System.Collections.ObjectModel.ObservableCollection<SearchResult> observable)
        {
            observable.Move(from, to);
            return;
        }

        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
    }
}

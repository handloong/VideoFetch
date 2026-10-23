using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VideoFetch.Views;

/// <summary>
/// 判断一次鼠标事件的"原始目标"是不是落在列表的某一行里。
///
/// MouseDoubleClick 是冒泡事件：双击表头（想排序）、双击行下方的空白、
/// 双击滚动条，都会进到同一个处理函数。以前不看来源、直接拿 SelectedItem，
/// 于是"双击表头排序"会顺带打开上一次选中的那个视频。
/// </summary>
public static class RowHitTest
{
    public static bool IsRow(object? originalSource)
    {
        if (originalSource is not DependencyObject node) return false;

        while (node != null)
        {
            if (node is ListViewItem) return true;

            // 走到列表本身还没遇到行 → 点的是表头/空白/滚动条
            if (node is ListView) return false;

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }
}

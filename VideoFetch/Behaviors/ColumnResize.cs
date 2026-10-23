using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VideoFetch.Behaviors;

/// <summary>
/// 让 GridView 的列宽可以拖动调整。
///
/// 为什么不用 WPF 自带的：默认模板里的 Thumb 叫 PART_HeaderGrip，控件内部会去
/// 认这个名字并挂 DragDelta。但主题里 GridViewColumnHeader 换成了自定义模板，
/// 补回同名 Thumb 之后**光标会变、列宽却不动**（实测 200 → 200），说明内部
/// 钩子并没有接上。与其猜内部实现，不如自己接管：Thumb 上挂 DragDelta，
/// 直接改 GridViewColumn.Width。
///
/// 用法（在 Thumb 上）：behaviors:ColumnResize.Enabled="True"
/// </summary>
public static class ColumnResize
{
    /// <summary>列宽下限：再窄表头文字就没意义了。</summary>
    public const double MinWidth = 50;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ColumnResize),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value)
        => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element)
        => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Thumb thumb) return;

        thumb.DragDelta -= OnDragDelta;
        if (e.NewValue is true) thumb.DragDelta += OnDragDelta;
    }

    private static void OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb) return;
        if (thumb.TemplatedParent is not GridViewColumnHeader header) return;
        if (header.Column is not GridViewColumn column) return;

        // 列宽可能是 Auto（NaN），那就从当前实际宽度起步
        var current = double.IsNaN(column.Width) ? header.ActualWidth : column.Width;
        column.Width = Math.Max(MinWidth, current + e.HorizontalChange);
    }
}

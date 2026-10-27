using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QoderSecs.App.ViewModels;

namespace QoderSecs.App;

/// <summary>
/// 类型菜单的排布：同一组占一行，组间留 10px 并画一条分隔线。
/// 15 个代号按基础类型、有符号、无符号、浮点分成四行；
/// 用 Panel 而不是 WrapPanel，是因为分隔线必须画在「组与组之间」，
/// WrapPanel 只认位置、不认分组。
/// </summary>
public sealed class TypeMenuPanel : Panel
{
    public const double CellWidth = 76;
    public const double CellHeight = 28;
    public const double RowHeight = 30;
    public const double GroupGap = 10;

    private const double SeparatorInset = 14;

    public static readonly DependencyProperty SeparatorBrushProperty = DependencyProperty.Register(
        nameof(SeparatorBrush),
        typeof(Brush),
        typeof(TypeMenuPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? SeparatorBrush
    {
        get => (Brush?)GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = BuildRows();
        if (rows.Count == 0)
        {
            return new Size(0, 0);
        }

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(CellWidth, CellHeight));
        }

        var columns = rows.Max(row => row.Count);
        return new Size(columns * CellWidth, RowsHeight(rows.Count));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rows = BuildRows();
        var y = 0.0;
        foreach (var row in rows)
        {
            for (var column = 0; column < row.Count; column++)
            {
                row[column].Arrange(new Rect(column * CellWidth, y, CellWidth, RowHeight));
            }

            y += RowHeight + GroupGap;
        }

        var width = rows.Count == 0 ? 0 : rows.Max(row => row.Count) * CellWidth;
        var height = rows.Count == 0 ? 0 : RowsHeight(rows.Count);
        return new Size(Math.Max(finalSize.Width, width), Math.Max(finalSize.Height, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (SeparatorBrush is null)
        {
            return;
        }

        var rows = BuildRows();
        if (rows.Count < 2)
        {
            return;
        }

        var pen = new Pen(SeparatorBrush, 1);
        for (var index = 1; index < rows.Count; index++)
        {
            // +0.5 让 1px 线落在像素中间，不然 100% 缩放下会糊成两行灰。
            var lineY = index * (RowHeight + GroupGap) - GroupGap / 2 + 0.5;
            drawingContext.DrawLine(
                pen,
                new Point(SeparatorInset, lineY),
                new Point(ActualWidth - SeparatorInset, lineY));
        }
    }

    private static double RowsHeight(int rowCount) =>
        rowCount * RowHeight + (rowCount - 1) * GroupGap;

    /// <summary>
    /// 按相邻同组切成行。子项的分组读数据项（TypePicker 的按钮和菜单项
    /// 都把 ItemTypeOption 放在 DataContext 上），读不到再退回 Tag。
    /// </summary>
    private List<List<UIElement>> BuildRows()
    {
        var rows = new List<List<UIElement>>();
        ItemTypeGroup? current = null;
        foreach (UIElement child in InternalChildren)
        {
            var group = GroupOf(child);
            if (rows.Count == 0 || group != current)
            {
                rows.Add([]);
                current = group;
            }

            rows[^1].Add(child);
        }

        return rows;
    }

    private static ItemTypeGroup? GroupOf(UIElement child)
    {
        if (child is not FrameworkElement element)
        {
            return null;
        }

        return (element.DataContext as ItemTypeOption)?.Group
            ?? (element.Tag as ItemTypeOption)?.Group;
    }
}

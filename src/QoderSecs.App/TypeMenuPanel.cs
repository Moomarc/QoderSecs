using System.Windows;
using System.Windows.Controls;

namespace QoderSecs.App;

/// <summary>
/// 类型菜单的排布：15 个代号固定 5 列，排 3 行，格子尺寸一致。
/// 用 Panel 而不是 UniformGrid，是因为格子要定宽，UniformGrid 会把它们拉满可用宽度。
/// </summary>
public sealed class TypeMenuPanel : Panel
{
    public const int Columns = 5;
    public const double CellWidth = 76;
    public const double CellHeight = 28;
    public const double RowHeight = 30;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(CellWidth, CellHeight));
        }

        return DesiredSizeFor(InternalChildren.Count);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var row = index / Columns;
            var column = index % Columns;
            InternalChildren[index].Arrange(
                new Rect(column * CellWidth, row * RowHeight, CellWidth, RowHeight));
        }

        var size = DesiredSizeFor(InternalChildren.Count);
        return new Size(Math.Max(finalSize.Width, size.Width), Math.Max(finalSize.Height, size.Height));
    }

    private static Size DesiredSizeFor(int count)
    {
        if (count == 0)
        {
            return new Size(0, 0);
        }

        var rows = (count + Columns - 1) / Columns;
        var columns = Math.Min(count, Columns);
        return new Size(columns * CellWidth, rows * RowHeight);
    }
}

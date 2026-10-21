using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using QoderSecs.Core.Messages;

namespace QoderSecs.App;

/// <summary>
/// 拖动时贴在目标行上的落点提示：上段画一条顶线，下段画一条底线，中段描一圈。
/// 只用画，不接鼠标，不改变行本身的样子。
/// </summary>
internal sealed class DropHintAdorner : Adorner
{
    private static readonly Brush HintBrush = Frozen(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly Brush MiddleFill = Frozen(Color.FromArgb(0x18, 0x25, 0x63, 0xEB));
    private static readonly Pen HintPen = FrozenPen(HintBrush, 2);
    private static readonly Pen MiddlePen = FrozenPen(HintBrush, 1.5);

    private DropZone _zone;

    public DropHintAdorner(UIElement adornedElement)
        : base(adornedElement)
    {
        IsHitTestVisible = false;
    }

    public DropZone Zone
    {
        get => _zone;
        set
        {
            if (_zone == value)
            {
                return;
            }

            _zone = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = AdornedElement.RenderSize;
        switch (_zone)
        {
            case DropZone.Top:
                drawingContext.DrawLine(HintPen, new Point(0, 1), new Point(size.Width, 1));
                drawingContext.DrawEllipse(HintBrush, null, new Point(3, 1), 3, 3);
                break;
            case DropZone.Bottom:
                var bottom = Math.Max(1, size.Height - 1);
                drawingContext.DrawLine(HintPen, new Point(0, bottom), new Point(size.Width, bottom));
                drawingContext.DrawEllipse(HintBrush, null, new Point(3, bottom), 3, 3);
                break;
            default:
                var inner = new Rect(1, 1, Math.Max(0, size.Width - 2), Math.Max(0, size.Height - 2));
                drawingContext.DrawRoundedRectangle(MiddleFill, MiddlePen, inner, 3, 3);
                break;
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}

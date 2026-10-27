using System.Windows.Media;
using QoderSecs.Core.Messages;

namespace QoderSecs.App;

/// <summary>
/// 元素类型的那套颜色，结构树的色块和类型下拉的色条共用一份。
/// 分开写两份的话，下拉里 U4 是蓝的、树里是另一个蓝，对照起来反而更乱。
/// </summary>
public static class TypePalette
{
    public static SolidColorBrush List { get; } = Frozen(0x64, 0x74, 0x8B);

    public static SolidColorBrush Text { get; } = Frozen(0x16, 0xA3, 0x4A);

    public static SolidColorBrush Binary { get; } = Frozen(0xD9, 0x77, 0x06);

    public static SolidColorBrush Boolean { get; } = Frozen(0x93, 0x33, 0xEA);

    public static SolidColorBrush Unknown { get; } = Frozen(0x9C, 0xA3, 0xAF);

    public static SolidColorBrush Numeric { get; } = Frozen(0x25, 0x63, 0xEB);

    public static SolidColorBrush Of(SecsItemType? type) => type switch
    {
        SecsItemType.List => List,
        SecsItemType.A or SecsItemType.J => Text,
        SecsItemType.B => Binary,
        SecsItemType.Boolean => Boolean,
        null => Unknown,
        _ => Numeric
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

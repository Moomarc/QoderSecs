using System.Globalization;
using System.Xml.Linq;

namespace QoderSecs.Core.Messages;

public sealed class StructureNode
{
    public StructureNode(XElement element)
    {
        Element = element;
        ElementName = element.Name.LocalName;
        HasKnownType = SecsItemTypes.TryParse(ElementName, out var type);
        ItemType = HasKnownType ? type : null;
        Children = element.Elements()
            .Where(child => child.Name != "Value" && child.Name != "Maps")
            .Select(child => new StructureNode(child))
            .ToList();
        Values = element.Elements("Value").Select(value => value.Value).ToList();
    }

    public XElement Element { get; }

    public static StructureNode? FromStructure(XElement? structure)
    {
        var item = structure?.Elements()
            .FirstOrDefault(child => child.Name != "Value" && child.Name != "Maps");
        return item is null ? null : new StructureNode(item);
    }

    public string ElementName { get; }

    public SecsItemType? ItemType { get; }

    public bool HasKnownType { get; }

    public string? Name => Element.Attribute("Name")?.Value;

    public IReadOnlyList<StructureNode> Children { get; }

    public IReadOnlyList<string> Values { get; }

    /// <summary>
    /// 第一个 &lt;Value&gt; 的文本，没有就是空串；数值类型的空占位按 "0" 返回，
    /// 和 ValidateNumber 放行空值的规则配套，转换时不用再判一次。
    /// </summary>
    public string SingleValue
    {
        get
        {
            var value = Values.Count == 0 ? string.Empty : Values[0];
            return string.IsNullOrWhiteSpace(value) && IsNumeric(ItemType) ? "0" : value;
        }
    }

    public bool IsArray => Values.Count > 1;

    public bool IsEditable => HasKnownType && !IsArray;

    /// <summary>
    /// 值输入框能不能编辑。L 本身没有值，数组和未知类型 v1 不碰，
    /// 界面显不显示输入框和 SetValue 返不返回 false 必须是同一条判断。
    /// </summary>
    public bool IsValueEditable => IsEditable && ItemType != SecsItemType.List;

    public string? ValidationError => Validate();

    private string? Validate()
    {
        if (!HasKnownType)
        {
            return $"未知元素类型「{ElementName}」，v1 不编辑。";
        }

        if (IsArray)
        {
            return "v1 不支持数值数组，保持原样不可编辑。";
        }

        var value = Values.Count == 0 ? null : Values[0];
        return ItemType switch
        {
            SecsItemType.List => Values.Count == 0 ? null : "L 不能带 <Value>。",
            SecsItemType.A or SecsItemType.J => null,
            SecsItemType.B => ValidateBinary(value),
            SecsItemType.Boolean => ValidateBoolean(value),
            _ => ValidateNumber(value)
        };
    }

    /// <summary>刻意和 Validate 里的 default 分支保持同一套判断：除这五种之外的类型都走数值校验。</summary>
    private static bool IsNumeric(SecsItemType? type) => type switch
    {
        null or SecsItemType.List or SecsItemType.A or SecsItemType.J or SecsItemType.B or SecsItemType.Boolean => false,
        _ => true
    };

    private string? ValidateNumber(string? value)
    {
        // SecsExpress 的模板消息经常把数值留空当占位（s6800.sxml 里 S2F35、S5F3、s5f5 就是这样），
        // 这种空值按 0 处理，不拦；SmlConverter 发送时同样按 0 转换。
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var styles = NumberStyles.Float | NumberStyles.AllowThousands;
        var culture = CultureInfo.InvariantCulture;
        var ok = ItemType switch
        {
            SecsItemType.I1 => sbyte.TryParse(value, out _),
            SecsItemType.I2 => short.TryParse(value, out _),
            SecsItemType.I4 => int.TryParse(value, out _),
            SecsItemType.I8 => long.TryParse(value, out _),
            SecsItemType.U1 => byte.TryParse(value, out _),
            SecsItemType.U2 => ushort.TryParse(value, out _),
            SecsItemType.U4 => uint.TryParse(value, out _),
            SecsItemType.U8 => ulong.TryParse(value, out _),
            SecsItemType.F4 => float.TryParse(value, styles, culture, out _),
            SecsItemType.F8 => double.TryParse(value, styles, culture, out _),
            _ => false
        };

        return ok ? null : $"「{value}」不是合法的 {ElementName} 值。";
    }

    /// <summary>
    /// SecsExpress 把长的 B 值写成空格分隔的十六进制字节（s6800.sxml 的 S7F6 就是），
    /// 所以校验和转换前都先去掉空白，规则只有这一处。
    /// </summary>
    public static string NormalizeBinary(string? value)
    {
        return value is null ? string.Empty : new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    private static string? ValidateBinary(string? value)
    {
        var hex = NormalizeBinary(value);
        if (hex.Length == 0)
        {
            return null;
        }

        if (hex.Length % 2 != 0 || hex.Any(c => !Uri.IsHexDigit(c)))
        {
            return "B 的值必须是成对的十六进制字符，可以用空格分隔。";
        }

        return null;
    }

    private static string? ValidateBoolean(string? value)
    {
        return bool.TryParse(value, out _) ? null : "Boolean 的值必须是 True 或 False。";
    }
}

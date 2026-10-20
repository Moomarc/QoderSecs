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

        // 多个 <Value> 子元素并不存在这个模型里：SecsExpress 自己的四份 sxml 一个例子都没有，
        // 数组一律写成单个 <Value>、各个值之间用空白分开（照 SecsExpress 的存法）。
        // 读入时真碰上多个就拼成一段文本，不让它变成不可编辑的第二种形状。
        var valueElements = element.Elements("Value").ToList();
        HasValue = valueElements.Count > 0;
        ValueText = string.Join(' ', valueElements.Select(value => value.Value));
        Items = SecsItemTypes.IsElementArray(ItemType)
            ? ValueText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            : [];
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

    /// <summary>&lt;Value&gt; 里的原文，多个值就是空格分隔的那一段。没有 Value 子元素时是空串。</summary>
    public string ValueText { get; }

    /// <summary>有没有 &lt;Value&gt; 子元素。L 带了个空的 Value 也算带，这一条要和"有没有正文"分开判。</summary>
    public bool HasValue { get; }

    /// <summary>数值和 Boolean 按空白拆出来的各个值；A / J / B / L 为空，它们的正文整体看 <see cref="ValueText"/>。</summary>
    public IReadOnlyList<string> Items { get; }

    /// <summary>
    /// &lt;Value&gt; 里的文本，没有就是空串；数值类型的空占位按 "0" 返回，
    /// 和 ValidateNumber 放行空值的规则配套，转换时不用再判一次。
    /// </summary>
    public string SingleValue =>
        string.IsNullOrWhiteSpace(ValueText) && IsNumeric(ItemType) ? "0" : ValueText;

    public bool IsArray => Items.Count > 1;

    /// <summary>这个类型能不能装多个值。A / J / B 是一段字节文本，不在数组这一档里。</summary>
    public bool CanHoldArray => SecsItemTypes.IsElementArray(ItemType);

    public bool IsEditable => HasKnownType;

    /// <summary>
    /// 值输入框能不能编辑。L 本身没有值，未知类型 v1 不碰，
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

        if (ItemType == SecsItemType.List)
        {
            return HasValue ? "L 不能带 <Value>。" : null;
        }

        if (IsArray)
        {
            // 逐个值判，报出是第几个出错：机台的数组动辄几十个值，只说「不合法」查不动。
            for (var index = 0; index < Items.Count; index++)
            {
                var error = ValidateOne(Items[index]);
                if (error is not null)
                {
                    return $"第 {index + 1} 个值有问题：{error}";
                }
            }

            return null;
        }

        return ValidateOne(HasValue ? ValueText : null);
    }

    private string? ValidateOne(string? value) => ItemType switch
    {
        SecsItemType.A or SecsItemType.J => null,
        SecsItemType.B => ValidateBinary(value),
        SecsItemType.Boolean => ValidateBoolean(value),
        _ => ValidateNumber(value)
    };

    /// <summary>走数值校验的那些类型：数组判断的补集，减掉 Boolean。和 Validate 的 default 分支同源。</summary>
    private static bool IsNumeric(SecsItemType? type) => SecsItemTypes.IsElementArray(type) && type != SecsItemType.Boolean;

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

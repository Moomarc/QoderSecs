using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace QoderSecs.Core.Messages;

public sealed class StructureNode
{
    /// <summary>超过这个字符数的值不在编辑框里展开；界面和 Core 用同一条口径。</summary>
    public const int LargeValueCharacterThreshold = 256 * 1024;

    private bool _validationComputed;
    private string? _validationError;
    private string? _normalizedBinary;
    private string? _valueSizeText;

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

    /// <summary>
    /// 一份快照只验一次。大 B 的校验要扫完整段正文，界面上好几处绑定都会读它，
    /// 每次重算等于同一段几十兆的文本被反复扫。
    /// </summary>
    public string? ValidationError
    {
        get
        {
            if (!_validationComputed)
            {
                _validationError = Validate();
                _validationComputed = true;
            }

            return _validationError;
        }
    }

    /// <summary>大值不在输入框里展开：几十兆文本交给 TextBox 排版会把界面拖死。</summary>
    public bool IsLargeValue => IsValueEditable && ValueText.Length > LargeValueCharacterThreshold;

    /// <summary>B 值去掉空白后的紧凑串，校验、发送、大小统计共用一份，不反复复制大正文。</summary>
    public string NormalizedBinary => _normalizedBinary ??= NormalizeBinary(ValueText);

    /// <summary>大值摘要里报的大小。B 按字节数，A / J 按字符数。</summary>
    public string ValueSizeText
    {
        get
        {
            if (_valueSizeText is not null)
            {
                return _valueSizeText;
            }

            if (ItemType == SecsItemType.B)
            {
                var bytes = NormalizedBinary.Length / 2;
                _valueSizeText = $"{FormatBytes(bytes)}（{bytes.ToString("N0", CultureInfo.InvariantCulture)} 字节）";
            }
            else
            {
                _valueSizeText = $"{ValueText.Length.ToString("N0", CultureInfo.InvariantCulture)} 个字符";
            }

            return _valueSizeText;
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes < 1024 * 1024
            ? $"{bytes / 1024.0:F1} KB"
            : $"{bytes / 1024.0 / 1024.0:F1} MB";

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
    /// 负数同样会先按 <see cref="ConvertNegativeBytes"/> 转掉，这样校验和发送都不会把 -45 当成坏值；
    /// 日志里的 0x00 前缀也在这里一并去掉。
    /// </summary>
    public static string NormalizeBinary(string? value)
    {
        var converted = NormalizeBinaryText(value);
        return converted.Length == 0 ? string.Empty : new string(converted.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    /// <summary>
    /// 收框时给用户看的 B 文本：去掉每个字节前面的 0x、负数换二补码，原来的空白分隔照留。
    /// 日志里印的是 0x00 0x01 形式，粘回编辑框必须和直接写 00 01 的一样用。
    /// 0x 后面没有十六进制数字的保持原样，交给校验报错，不能悄悄变成空值。
    /// </summary>
    public static string NormalizeBinaryText(string? value) =>
        ConvertNegativeBytes(ExpandBinaryPrefixes(value));

    private static readonly Regex BinaryPrefix = new(
        @"(?<![^\s])0[xX](?=[0-9A-Fa-f])",
        RegexOptions.Compiled);

    private static string ExpandBinaryPrefixes(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("0x", StringComparison.OrdinalIgnoreCase))
        {
            return value ?? string.Empty;
        }

        return BinaryPrefix.Replace(value, string.Empty);
    }

    private static readonly Regex NegativeByteToken = new(
        @"(?<!\S)-(?<value>\d{1,3})(?!\S)",
        RegexOptions.Compiled);

    /// <summary>
    /// 把 B 值里单独成项的负数按 8 位二补码换成两位大写十六进制：-45 → D3、-1 → FF、-127 → 81。
    /// 只认 -1 到 -127；-128 及其他值原样留着，让校验照常报红。
    /// 保留原来的空白分隔——这是收框时给用户看的文本，不是最终发送用的紧凑串。
    /// </summary>
    public static string ConvertNegativeBytes(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('-'))
        {
            return value ?? string.Empty;
        }

        return NegativeByteToken.Replace(value, match =>
        {
            if (!int.TryParse(
                    match.Groups["value"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number)
                || number is < 1 or > 127)
            {
                return match.Value;
            }

            return ((byte)-number).ToString("X2", CultureInfo.InvariantCulture);
        });
    }

    private const string BinaryError = "B 的值必须是成对的十六进制字符，可以用空格分隔，0x 前缀可以带。";

    /// <summary>
    /// 逐项扫一遍，不拼出紧凑串：这个判断会被界面反复读，几十兆的正文不该为它再复制一份。
    /// 规则和 <see cref="NormalizeBinaryText"/> 对齐：空白分隔、每项可以带 0x、-1 到 -127 按二补码算两个字节，
    /// 全部项合起来的十六进制位数必须是偶数。
    /// </summary>
    private static string? ValidateBinary(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        long digits = 0;
        for (var index = 0; index < value.Length;)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < value.Length && !char.IsWhiteSpace(value[index]))
            {
                index++;
            }

            var token = value.AsSpan(start, index - start);
            if (IsNegativeByteToken(token, out var isConvertible))
            {
                if (!isConvertible)
                {
                    return BinaryError;
                }

                digits += 2;
                continue;
            }

            if (token.Length >= 3
                && token[0] == '0'
                && (token[1] == 'x' || token[1] == 'X')
                && IsHexDigit(token[2]))
            {
                token = token[2..];
            }

            foreach (var c in token)
            {
                if (!IsHexDigit(c))
                {
                    return BinaryError;
                }
            }

            digits += token.Length;
        }

        return digits % 2 == 0 ? null : BinaryError;
    }

    /// <summary>
    /// 这一项像不像单独成项的负数。-1 到 -127 才算能转，-128、-0 这类仍交给后面的十六进制检查报错。
    /// 返回 false 表示根本不像负数字节，按普通十六进制项继续扫。
    /// </summary>
    private static bool IsNegativeByteToken(ReadOnlySpan<char> token, out bool isConvertible)
    {
        isConvertible = false;
        if (token.Length < 2 || token[0] != '-')
        {
            return false;
        }

        var digits = token[1..];
        if (digits.Length > 3)
        {
            return false;
        }

        var number = 0;
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            number = number * 10 + (c - '0');
        }

        isConvertible = number is >= 1 and <= 127;
        return true;
    }

    private static bool IsHexDigit(char c) =>
        c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static string? ValidateBoolean(string? value)
    {
        return bool.TryParse(value, out _) ? null : "Boolean 的值必须是 True 或 False。";
    }
}

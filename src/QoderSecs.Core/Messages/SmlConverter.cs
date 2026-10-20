using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Secs4Net;
using Secs4Net.Sml;

namespace QoderSecs.Core.Messages;

public static class SmlConverter
{
    public static bool TryToSml(MessageEntry entry, out string sml, out string? error)
    {
        sml = string.Empty;
        if (!TryToMessage(entry, out var message, out error))
        {
            return false;
        }

        using (message)
        {
            sml = ToSml(message);
        }

        return true;
    }

    /// <summary>
    /// 只判断这条消息发不发得出去（值转不转得成 SECS 数据），不生成正文。
    /// 发送按钮的可用性每敲一下都要判一次，大 PP body 不能为这个攒出几十兆文本。
    /// </summary>
    public static bool TryValidate(MessageEntry entry, [NotNullWhen(false)] out string? error)
    {
        if (!TryToMessage(entry, out var message, out error))
        {
            return false;
        }

        message.Dispose();
        return true;
    }

    /// <summary>
    /// 预览用的 SML：正文超过上限就不生成，改给一句说明。
    /// 预览框里塞几十兆文本，界面画不动也滚不动，正文本身在 sml.log 里才是完整的。
    /// </summary>
    public static bool TryToPreviewSml(MessageEntry entry, int characterCap, out string sml, out string? error)
    {
        sml = string.Empty;
        if (!TryToMessage(entry, out var message, out error))
        {
            return false;
        }

        using (message)
        {
            var estimate = EstimateTextBytes(message);
            if (estimate > characterCap)
            {
                sml = $"正文约 {SizeText(estimate)}，超过预览上限 {SizeText(characterCap)}，这里不展开。" +
                      $"{Environment.NewLine}结构和值以「结构」页签为准，发送和 sml.log 落盘都不受影响。";
                return true;
            }

            sml = ToSml(message);
        }

        return true;
    }

    /// <summary>给人看的大小。连接层降级成"没有正文"的记录时也要报出正文有多大。</summary>
    internal static string SizeText(long bytes) =>
        bytes < 1024 * 1024
            ? $"{bytes / 1024} KB"
            : $"{bytes / 1024.0 / 1024.0:F1} MB";

    /// <summary>
    /// 生成 SML 时抹掉名称：SmlWriter 会把名称写在首行，那种形式既不像 SML 也粘不回解析器。
    /// 名称由通信记录的 SecsHeader 承担，SECS-II 头里本来就没有名称字段。
    /// </summary>
    public static string ToSml(SecsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 这份副本故意不 Dispose：SecsItem 属于调用方那条消息，Dispose 会把它一起释放掉。
        var nameless = new SecsMessage(message.S, message.F, message.ReplyExpected)
        {
            SecsItem = message.SecsItem
        };
        return SmlWriter.ToSml(nameless);
    }

    /// <summary>
    /// 大消息走这里：边格式化边写进 writer，不在内存里攒出整份文本。
    /// 调用方要保证 message 在本方法完成前不被释放。
    /// </summary>
    public static Task WriteSmlAsync(TextWriter writer, SecsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var nameless = new SecsMessage(message.S, message.F, message.ReplyExpected)
        {
            SecsItem = message.SecsItem
        };
        return SmlWriter.WriteSmlToAsync(nameless, writer);
    }

    /// <summary>
    /// 估算转成 SML 文本后的字节数，用来决定这条消息该内联还是只落盘。
    /// 只需要数量级对：B 的 SML 是十六进制，约为原始字节的三倍。
    /// </summary>
    public static long EstimateTextBytes(SecsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.SecsItem is { } item ? EstimateItem(item) + 64 : 64;
    }

    private static long EstimateItem(Item item)
    {
        if (item.Format == SecsFormat.List)
        {
            return 8 + item.Items.Sum(EstimateItem);
        }

        return item.Format switch
        {
            SecsFormat.Binary => RawLength(item) * 3L + 16,
            SecsFormat.ASCII or SecsFormat.JIS8 => RawLength(item) + 16,
            _ => RawLength(item) * 4L + 16
        };
    }

    /// <summary>
    /// 取值的原始长度。ASCII 这类格式不给原始内存，只能拿文本，
    /// 大正文的代价是一次临时字符串，比把整条消息转成 SML 便宜得多。
    /// </summary>
    private static long RawLength(Item item)
    {
        try
        {
            return item.GetMemory<byte>().Length;
        }
        catch (NotSupportedException)
        {
            return item.GetString()?.Length ?? 0;
        }
    }

    /// <summary>把库里的消息变成一条待发送的 SECS 消息；值不合法时返回 false 并给出原因。</summary>
    public static bool TryToMessage(
        MessageEntry entry,
        [NotNullWhen(true)] out SecsMessage? message,
        [NotNullWhen(false)] out string? error) => TryToMessage(entry, entry.WBit, out message, out error);

    /// <summary>
    /// W 位可以强制指定：自动回复发出去的是 Secondary，库里那条即使勾了 W 也不能带上线。
    /// </summary>
    public static bool TryToMessage(
        MessageEntry entry,
        bool replyExpected,
        [NotNullWhen(true)] out SecsMessage? message,
        [NotNullWhen(false)] out string? error)
    {
        message = null;
        error = null;

        Item? item = null;
        var root = StructureNode.FromStructure(entry.Structure);
        if (root is not null && !TryToItem(root, out item, out error))
        {
            return false;
        }

        var built = new SecsMessage((byte)entry.Stream, (byte)entry.Function, replyExpected)
        {
            Name = entry.Name
        };
        if (item is not null)
        {
            built.SecsItem = item;
        }

        message = built;
        return true;
    }

    private static bool TryToItem(
        StructureNode node,
        out Item? item,
        [NotNullWhen(false)] out string? error)
    {
        item = null;
        error = null;
        if (node.ValidationError is not null)
        {
            error = node.ValidationError;
            return false;
        }

        var value = node.SingleValue;
        // 数组的各个值。空的数值占位没有值可拆，退成 SingleValue 那一个 0，和 ValidateNumber
        // 放行空值、SmlParser 允许 <U4 [1] > 是同一条口径。
        var values = node.Items.Count > 0 ? node.Items : [value];
        var culture = CultureInfo.InvariantCulture;
        switch (node.ItemType)
        {
            case SecsItemType.List:
                var children = new List<Item>();
                foreach (var child in node.Children)
                {
                    if (!TryToItem(child, out var childItem, out error))
                    {
                        return false;
                    }

                    children.Add(childItem!);
                }

                item = Item.L(children);
                return true;
            case SecsItemType.A:
                item = Item.A(value);
                return true;
            case SecsItemType.J:
                item = Item.J(value);
                return true;
            case SecsItemType.B:
                item = Item.B(Convert.FromHexString(StructureNode.NormalizeBinary(value)));
                return true;
            case SecsItemType.Boolean:
                item = Item.Boolean(ParseEach(values, bool.Parse));
                return true;
            case SecsItemType.I1:
                item = Item.I1(ParseEach(values, text => sbyte.Parse(text, culture)));
                return true;
            case SecsItemType.I2:
                item = Item.I2(ParseEach(values, text => short.Parse(text, culture)));
                return true;
            case SecsItemType.I4:
                item = Item.I4(ParseEach(values, text => int.Parse(text, culture)));
                return true;
            case SecsItemType.I8:
                item = Item.I8(ParseEach(values, text => long.Parse(text, culture)));
                return true;
            case SecsItemType.U1:
                item = Item.U1(ParseEach(values, text => byte.Parse(text, culture)));
                return true;
            case SecsItemType.U2:
                item = Item.U2(ParseEach(values, text => ushort.Parse(text, culture)));
                return true;
            case SecsItemType.U4:
                item = Item.U4(ParseEach(values, text => uint.Parse(text, culture)));
                return true;
            case SecsItemType.U8:
                item = Item.U8(ParseEach(values, text => ulong.Parse(text, culture)));
                return true;
            case SecsItemType.F4:
                item = Item.F4(ParseEach(values, text => float.Parse(text, culture)));
                return true;
            case SecsItemType.F8:
                item = Item.F8(ParseEach(values, text => double.Parse(text, culture)));
                return true;
            default:
                error = $"未知元素类型「{node.ElementName}」，v1 不编辑。";
                return false;
        }
    }

    /// <summary>
    /// 逐个转成该类型的值。走到这一步 ValidationError 已经放行，解析不会再失败；
    /// 真有人绕过校验塞进来，异常由调用链上的发送路径接住并报成发送失败。
    /// </summary>
    private static T[] ParseEach<T>(IReadOnlyList<string> values, Func<string, T> parse)
    {
        var parsed = new T[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            parsed[index] = parse(values[index]);
        }

        return parsed;
    }
}

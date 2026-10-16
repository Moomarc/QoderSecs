using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace QoderSecs.Core.Messages;

public sealed class MessageLibrary
{
    private XDocument _document;

    private MessageLibrary(XDocument document)
    {
        _document = document;
        Messages = ParseMessages(document);
    }

    public IReadOnlyList<MessageEntry> Messages { get; private set; }

    public XDocument Document => _document;

    public static MessageLibrary Empty => new(CreateEmptyDocument());

    public static MessageLibrary Load(string xml)
    {
        return new(XDocument.Parse(xml, LoadOptions.PreserveWhitespace));
    }

    public string Export() => _document.ToString(SaveOptions.DisableFormatting);

    public IReadOnlyList<MessageEntry> Query(string? search)
    {
        var sorted = Messages
            .OrderBy(entry => entry.Stream)
            .ThenBy(entry => entry.Function)
            .ThenBy(entry => entry.Index)
            .ToList();

        var query = search?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return sorted;
        }

        if (TryParseSignature(query, out var stream, out var function))
        {
            return sorted
                .Where(entry => entry.Stream == stream && entry.Function == function)
                .ToList();
        }

        return sorted
            .Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static readonly Regex SignaturePattern = new(
        @"^S?\s*(?<stream>\d{1,3})\s*(?:F|,|\s)\s*(?<function>\d{1,3})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryParseSignature(string query, out int stream, out int function)
    {
        stream = 0;
        function = 0;
        var match = SignaturePattern.Match(query);
        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Groups["stream"].Value, out stream)
            && int.TryParse(match.Groups["function"].Value, out function)
            && stream is >= 1 and <= 255
            && function is >= 1 and <= 255;
    }

    public void Clear()
    {
        _document = CreateEmptyDocument();
        Messages = ParseMessages(_document);
    }

    /// <summary>
    /// 在 MessageDictionary 末尾新建一条空消息（没有 WBit，Structure 是空的），返回它在 Messages 里的条目。
    /// 显示顺序由 Query 按 SxFy 排，XML 里的位置不影响。
    /// </summary>
    public MessageEntry NewMessage(int stream, int function, string name)
    {
        RequireRange(nameof(stream), "Stream", stream);
        RequireRange(nameof(function), "Function", function);

        var message = new XElement("Message",
            new XAttribute("Name", name),
            new XElement("Stream", stream.ToString(CultureInfo.InvariantCulture)),
            new XElement("Function", function.ToString(CultureInfo.InvariantCulture)),
            new XElement("Structure"));

        var dictionary = _document.Root!.Element("MessageDictionary")!;
        var last = dictionary.Elements("Message").LastOrDefault();
        if (last?.PreviousNode is XText separator && dictionary.LastNode is XText tail)
        {
            // 沿用文件里已有的换行缩进，否则新消息会和 </MessageDictionary> 挤成一行，
            // 同事拿文本工具比对 SXML 时会看到结尾整片不同。
            tail.AddBeforeSelf(new XText(separator.Value), message);
        }
        else
        {
            dictionary.Add(message);
        }

        Messages = ParseMessages(_document);
        return Messages[^1];
    }

    /// <summary>
    /// 用粘贴进来的 SML 覆盖一条消息的头和结构，其余内容（Name、Description、AutoReply、未知子元素）原样保留。
    /// </summary>
    public MessageEntry ApplySml(MessageEntry entry, ParsedSml parsed)
    {
        var message = entry.Element;
        message.Element("Stream")!.Value = parsed.Stream.ToString(CultureInfo.InvariantCulture);
        var function = message.Element("Function")!;
        function.Value = parsed.Function.ToString(CultureInfo.InvariantCulture);
        ApplyWBit(message, parsed.ReplyExpected);

        var structure = message.Element("Structure");
        if (structure is null)
        {
            structure = new XElement("Structure");
            message.Add(structure);
        }

        structure.RemoveNodes();
        structure.Add(parsed.Structure.Nodes());

        return Reparse(entry);
    }

    /// <summary>改消息名称。留空就把 Name 属性去掉，读回来是空字符串。</summary>
    public MessageEntry SetName(MessageEntry entry, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            entry.Element.Attribute("Name")?.Remove();
        }
        else
        {
            entry.Element.SetAttributeValue("Name", trimmed);
        }

        return Reparse(entry);
    }

    /// <summary>改消息头。Stream、Function、W 位一起改，免得界面上三个框各改一次要重建三遍列表。</summary>
    public MessageEntry SetHeader(MessageEntry entry, int stream, int function, bool wBit)
    {
        RequireRange(nameof(stream), "Stream", stream);
        RequireRange(nameof(function), "Function", function);

        var message = entry.Element;
        message.Element("Stream")!.Value = stream.ToString(CultureInfo.InvariantCulture);
        message.Element("Function")!.Value = function.ToString(CultureInfo.InvariantCulture);
        ApplyWBit(message, wBit);

        return Reparse(entry);
    }

    /// <summary>
    /// 改自动回复目标（库里另一条消息的 Name）。传 null 就把 AutoReply 元素删掉，
    /// 不留空元素，和 SecsExpress 的写法一致。
    /// </summary>
    public MessageEntry SetAutoReply(MessageEntry entry, string? autoReply)
    {
        var message = entry.Element;
        var element = message.Element("AutoReply");
        if (string.IsNullOrWhiteSpace(autoReply))
        {
            element?.Remove();
            return Reparse(entry);
        }

        if (element is null)
        {
            element = new XElement("AutoReply");
            (message.Element("WBit") ?? message.Element("Function")!).AddAfterSelf(element);
        }

        element.Value = autoReply.Trim();
        return Reparse(entry);
    }

    /// <summary>SecsExpress 只在要 W 的时候写 &lt;WBit&gt;1&lt;/WBit&gt;，不要 W 就整个元素没有。</summary>
    private static void ApplyWBit(XElement message, bool replyExpected)
    {
        var wBit = message.Element("WBit");
        if (!replyExpected)
        {
            wBit?.Remove();
            return;
        }

        if (wBit is null)
        {
            message.Element("Function")!.AddAfterSelf(new XElement("WBit", "1"));
        }
        else
        {
            wBit.Value = "1";
        }
    }

    /// <summary>
    /// MessageEntry 是一次性快照，改完 XML 必须重新解析，界面上拿到的才是新值。
    /// Index 是消息在 MessageDictionary 里的位置，这几个改法都不动位置。
    /// </summary>
    private MessageEntry Reparse(MessageEntry entry)
    {
        Messages = ParseMessages(_document);
        return Messages[entry.Index];
    }

    private static void RequireRange(string argumentName, string displayName, int value)
    {
        if (value is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(argumentName, value, $"{displayName} 必须在 1 到 255 之间。");
        }
    }

    private static XDocument CreateEmptyDocument() => new(
        new XElement("SXML",
            new XElement("DataDictionary"),
            new XElement("MessageDictionary")));

    private static IReadOnlyList<MessageEntry> ParseMessages(XDocument document)
    {
        var root = document.Root;
        if (root is null || root.Name.LocalName != "SXML")
        {
            throw new FormatException("根元素必须是 <SXML>。");
        }

        var dictionary = root.Element("MessageDictionary");
        if (dictionary is null)
        {
            throw new FormatException("缺少 <MessageDictionary>。");
        }

        var entries = new List<MessageEntry>();
        var index = 0;
        foreach (var message in dictionary.Elements("Message"))
        {
            entries.Add(ParseMessage(message, index));
            index++;
        }

        return entries;
    }

    private static MessageEntry ParseMessage(XElement message, int index)
    {
        var name = message.Attribute("Name")?.Value ?? string.Empty;
        return new MessageEntry(
            message,
            name,
            ReadInt(message, "Stream", name),
            ReadInt(message, "Function", name),
            message.Element("WBit")?.Value.Trim() == "1",
            message.Element("AutoReply")?.Value,
            message.Element("Structure"),
            index);
    }

    private static int ReadInt(XElement message, string tag, string name)
    {
        var raw = message.Element(tag)?.Value.Trim();
        if (raw is not null && int.TryParse(raw, out var value))
        {
            return value;
        }

        throw new FormatException($"消息「{name}」缺少有效的 <{tag}>。");
    }
}

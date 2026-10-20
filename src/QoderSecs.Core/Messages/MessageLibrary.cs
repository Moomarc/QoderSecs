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
        EnsureDataDictionary(document);
        Messages = ParseMessages(document);
        DataEntries = ParseDataEntries(document);
    }

    public IReadOnlyList<MessageEntry> Messages { get; private set; }

    /// <summary>
    /// <c>DataDictionary</c> 的直接子元素，就是 SecsExpress 自己那份「单独存的 data 和 list」。
    /// 这一段以前从不解析、整段原样保留，现在只读它和写它，不动别的段。
    /// </summary>
    public IReadOnlyList<DataEntry> DataEntries { get; private set; }

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
        DataEntries = ParseDataEntries(_document);
    }

    /// <summary>
    /// 在 MessageDictionary 末尾新建一条空消息（没有 WBit，Structure 是空的），返回它在 Messages 里的条目。
    /// 显示顺序由 Query 按 SxFy 排，XML 里的位置不影响。
    /// </summary>
    public MessageEntry NewMessage(int stream, int function, string name)
    {
        RequireRange(nameof(stream), "Stream", stream);
        RequireRange(nameof(function), "Function", function);
        var checkedName = RequireMessageName(name, target: null);

        var message = new XElement("Message",
            new XAttribute("Name", checkedName),
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
    /// 复制一条消息，插在原条后面。除名字以外全部原样带过来，包括 AutoReply、Description
    /// 和看不懂的未知子元素。
    /// 名字必须给一个库里没有的、而且不能为空——自动回复是按 Name 指向消息的，两条同名之后
    /// 指向落到哪一条变成看库里谁排在前面（<see cref="AutoReplyResolver"/>），
    /// 界面上的自动回复下拉框也会列出两个一样的选项。SxFy 想重复随便重复，名字不行。
    /// </summary>
    public MessageEntry DuplicateMessage(MessageEntry entry, string name)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var dictionary = _document.Root?.Element("MessageDictionary");
        if (dictionary is null || !ReferenceEquals(entry.Element.Parent, dictionary))
        {
            throw new ArgumentException("这条消息不在当前消息库里（库已换过）。", nameof(entry));
        }

        // 必须是深克隆：浅克隆会让副本和原条共用同一批子节点实例，改副本的结构连着改掉原条。
        var clone = new XElement(entry.Element);
        clone.SetAttributeValue("Name", RequireMessageName(name, target: null));

        if (entry.Element.PreviousNode is XText separator && string.IsNullOrWhiteSpace(separator.Value))
        {
            // 换行缩进跟着副本走一份，理由和 NewMessage、RemoveMessage 是同一条：
            // 同事拿文本工具比对 SXML 时不该看到消息挤成一行或者成片空行。
            // 前面是注释就不抄，未知内容原样保留比省一行空白更硬。
            entry.Element.AddAfterSelf(new XText(separator.Value), clone);
        }
        else
        {
            entry.Element.AddAfterSelf(clone);
        }

        Messages = ParseMessages(_document);
        return Messages.Single(candidate => ReferenceEquals(candidate.Element, clone));
    }

    /// <summary>
    /// 删掉一条消息，连它前面的换行缩进一起删——和 <see cref="NewMessage"/> 沿用缩进是同一条理由：
    /// 不删干净的话 MessageDictionary 里会留下成片空行，同事拿文本工具比对 SXML 会看到删除处整片不同。
    /// Index 是消息在 MessageDictionary 里的位置，删一条后面的全部前移，所以调用方手上的旧快照
    /// 不能再来定位，删除之后要按 <see cref="Messages"/> 重新取。
    /// </summary>
    public void RemoveMessage(MessageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var dictionary = _document.Root?.Element("MessageDictionary");
        if (dictionary is null || !ReferenceEquals(entry.Element.Parent, dictionary))
        {
            // MessageEntry 是一次性快照：库换过或者同一条删两次时，手上的元素已经不在当前文档里了。
            throw new ArgumentException("这条消息不在当前消息库里（库已换过，或者这条已经删掉了）。", nameof(entry));
        }

        var separator = entry.Element.PreviousNode;
        entry.Element.Remove();
        if (separator is XText text && string.IsNullOrWhiteSpace(text.Value))
        {
            text.Remove();
        }

        var leftovers = dictionary.Nodes().ToList();
        if (!dictionary.Elements("Message").Any() && leftovers.All(node => node is XText))
        {
            // 最后一条删完只剩空白节点，导出会是「<MessageDictionary>    </MessageDictionary>」。
            // 有注释就不动，未知内容原样保留是比空行更硬的口径。
            dictionary.RemoveNodes();
        }

        // 指着这条名字的 AutoReply 一并置空，和 SetName 同一条口径：目标没了，引用留着也指不到。
        ClearAutoRepliesPointingAt(entry.Name, entry);

        Messages = ParseMessages(_document);
    }

    /// <summary>
    /// 把指着这个名字的 AutoReply 全部置空——整个元素删掉，不留空元素，
    /// 和 <see cref="SetAutoReply"/> 传 null 时的形状一致。
    /// 谁指着它走 <see cref="AutoReplyResolver.FindReferrers"/>，和运行时取目标是同一份比法，
    /// 这里不另写第二遍（两处数法不一致就会出现「置空了但界面还显示指着」）。
    /// </summary>
    private void ClearAutoRepliesPointingAt(string? name, MessageEntry? except = null)
    {
        foreach (var referrer in AutoReplyResolver.FindReferrers(this, name, except))
        {
            referrer.Element.Element("AutoReply")?.Remove();
        }
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

    /// <summary>
    /// 改消息名称。名字非空、且不许多条消息共用（<see cref="RequireMessageName"/>），
    /// 所以这里不再像早先那样「留空就把 Name 属性去掉」。
    /// 指着旧名的那些 AutoReply 一并置空：引用是按名字存的，
    /// 名字一改它就指不到了，留着的话界面上看不出来、运行时也不会回，等于埋一个哑弹。
    /// </summary>
    public MessageEntry SetName(MessageEntry entry, string name)
    {
        var checkedName = RequireMessageName(name, entry.Element);
        var oldName = entry.Name;

        entry.Element.SetAttributeValue("Name", checkedName);
        if (!string.Equals(oldName.Trim(), checkedName, StringComparison.Ordinal))
        {
            // 名字没真变（只多敲了空格、或者改回原名）就不动别人的引用，
            // 否则「改回自己原来的名字」这一下会把指着它的自动回复全清掉。
            ClearAutoRepliesPointingAt(oldName);
        }

        return Reparse(entry);
    }

    /// <summary>库里有没有这个名字的消息（首尾空格不算）。</summary>
    public bool HasMessageName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 0
            && Messages.Any(entry => string.Equals(entry.Name, trimmed, StringComparison.Ordinal));
    }

    /// <summary>
    /// 取一个没被占用的消息名：先试 baseName 本身，占了就依次试「baseName 2」「baseName 3」。
    /// 新建和复制这两个自动取名的地方要靠它——名字必须唯一，按两次「新建」不能第二次就报错。
    /// </summary>
    public string NextFreeMessageName(string baseName)
    {
        ArgumentNullException.ThrowIfNull(baseName);

        var trimmed = baseName.Trim();
        if (trimmed.Length == 0)
        {
            trimmed = "消息";
        }

        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? trimmed : $"{trimmed} {attempt}";
            if (!HasMessageName(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 消息名的两条规则：非空、且不许多条共用。这是 SecsExpress 对消息体的硬要求
    /// （SecsExpress 实测），反过来 SxFy 可以重复——同一个 S2F41 可以有 START、STOP、
    /// PP_SELECT 三条，全靠名字区分，自动回复也按名字指向（<see cref="AutoReplyResolver"/>）。
    /// 读入不查这一条：同事的文件里就算有同名或没名字的消息也照样得能打开、能导出回去。
    /// </summary>
    private string RequireMessageName(string name, XElement? target)
    {
        ArgumentNullException.ThrowIfNull(name);

        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("消息必须有名字：同一个 SxFy 可以有多条，全靠名字区分。", nameof(name));
        }

        var clash = Messages.Any(entry =>
            !ReferenceEquals(entry.Element, target)
            && string.Equals(entry.Name, trimmed, StringComparison.Ordinal));
        if (clash)
        {
            throw new ArgumentException($"库里已经有名字叫「{trimmed}」的消息。SxFy 可以重复，名字不行。", nameof(name));
        }

        return trimmed;
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

    // ---------- 数据字典条目：单独存起来的 data 和 list ----------

    /// <summary>
    /// 把消息树里的一个节点存成条目，一律追加在末尾。
    /// 名字可空、可重（SecsExpress 实测：data/list 这类数据元素既不要求名字唯一，
    /// 也不要求非空，树里和字典里都一样；那两条只约束消息体）。传进来的名字为空就是把 Name 属性去掉。
    /// 条目和消息之间是复制关系，改条目碰不到任何一条已经写好的消息。
    /// 必须深拷贝，理由和 <see cref="DuplicateMessage"/> 同一条：浅拷贝会让条目和消息里的
    /// 那个节点共用同一批子节点实例，之后改一边另一边跟着变，复制语义就假了。
    /// </summary>
    public DataEntry SaveEntry(string name, XElement source)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Parent is null)
        {
            throw new ArgumentException("要存的必须是消息树里的一个节点，脱离树的元素没地方可去。", nameof(source));
        }

        var dictionary = RequireDataDictionary();
        var clone = new XElement(source);
        SetEntryName(clone, name);
        AddEntryNode(dictionary, clone);

        DataEntries = ParseDataEntries(_document);
        return DataEntries.Single(entry => ReferenceEquals(entry.Element, clone));
    }

    /// <summary>
    /// 在字典里新建一个条目，内容是这个类型必然合法的默认值（和结构页「添加子项」同一套，
    /// 建出来就不是红的）。名字可空可重，理由同 <see cref="SaveEntry"/>；留空就是没有 Name 属性，
    /// 界面上那一栏空白，靠类型和内容认。
    /// <paramref name="after"/> 给定时插在那一条之后，没给就追加在末尾。
    /// </summary>
    public DataEntry NewEntry(string name, SecsItemType type, DataEntry? after = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        var dictionary = RequireDataDictionary();
        var entry = StructureEditor.NewElement(type);
        SetEntryName(entry, name);

        if (after is null)
        {
            AddEntryNode(dictionary, entry);
        }
        else
        {
            RequireCurrentEntry(after);
            InsertEntryAfter(after.Element, entry);
        }

        DataEntries = ParseDataEntries(_document);
        return DataEntries.Single(parsed => ReferenceEquals(parsed.Element, entry));
    }

    /// <summary>改条目名字。留空是把 Name 属性去掉，不是拒；名字不参与任何查重。</summary>
    public DataEntry RenameEntry(DataEntry entry, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        RequireCurrentEntry(entry);

        SetEntryName(entry.Element, name);
        DataEntries = ParseDataEntries(_document);
        return DataEntries[entry.Index];
    }

    /// <summary>
    /// 名字落到元素上：留空就去掉 Name 属性，和 <see cref="DuplicateMessage"/>、
    /// 树上的就地改名（<see cref="StructureEditor.SetName"/>）是同一条写法。
    /// </summary>
    private static void SetEntryName(XElement entry, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            entry.Attribute("Name")?.Remove();
            return;
        }

        entry.SetAttributeValue("Name", trimmed);
    }

    /// <summary>
    /// 删掉条目，连它前面那段换行缩进一起删，和 <see cref="RemoveMessage"/> 同一条理由：
    /// 不留成片空行给同事比对。删完什么都不剩时留着 <c>&lt;DataDictionary&gt;</c> 换行
    /// <c>&lt;/DataDictionary&gt;</c> 那一格不塌成自闭合——SecsExpress 自己的空字典就是写成两行的
    /// （s2test.sxml、s6800.sxml 都是），照它来。
    /// </summary>
    public void RemoveEntry(DataEntry entry)
    {
        RequireCurrentEntry(entry);

        var dictionary = _document.Root!.Element("DataDictionary")!;
        var separator = entry.Element.PreviousNode;
        entry.Element.Remove();
        if (separator is XText text && string.IsNullOrWhiteSpace(text.Value))
        {
            text.Remove();
        }

        DataEntries = ParseDataEntries(_document);
    }

    /// <summary>
    /// 顶层条目被直接动过顺序之后（界面上拖动一条条目排到另一条后面就是这种），把 <see cref="DataEntries"/>
    /// 按当前文档重拍一遍。这一份是解析时拍的快照，只改 <c>DataDictionary</c> 里的 XElement 不会让它跟着变，
    /// 而字典页那棵树的行就是照这一份排的——不重拍，XML 已经换了次序，界面上还是老次序。
    /// </summary>
    public void SyncDataEntries() => DataEntries = ParseDataEntries(_document);

    /// <summary>字典段在 <see cref="Load"/> 时就保证有了，这里只取。</summary>
    private XElement RequireDataDictionary() => _document.Root!.Element("DataDictionary")!;

    /// <summary>
    /// 缺 <c>&lt;DataDictionary&gt;</c> 这一段的文件，SecsExpress 直接打不开：拿
    /// 单变量对照件在 SecsExpress 里实测过，六份里只有删掉这一段的和我们的样例库失败，
    /// 中文描述、LF 行尾、自闭合写法都打得开。所以读进来就把它补上，导出的文件一定带这一段。
    /// 写法照抄 SecsExpress 的空字典（两行、不自闭合），换行缩进沿用 <c>&lt;MessageDictionary&gt;</c>
    /// 前面那个文本节点，好让 CRLF 的文件不会被补进 LF。位置也跟着参考件放在消息段之前。
    /// </summary>
    private static void EnsureDataDictionary(XDocument document)
    {
        var root = document.Root;
        var messages = root?.Element("MessageDictionary");
        if (messages is null || root!.Element("DataDictionary") is not null)
        {
            return;
        }

        var indent = messages.PreviousNode is XText text && text.Value.Contains('\n') ? text.Value : "\n";
        messages.AddBeforeSelf(new XElement("DataDictionary", new XText(indent)), new XText(indent));
    }

    private static void AddEntryNode(XElement dictionary, XElement entry)
    {
        var last = dictionary.Elements().LastOrDefault();
        if (last?.PreviousNode is XText separator && dictionary.LastNode is XText tail)
        {
            // 沿用文件里已有的换行缩进，理由和 NewMessage 完全一样。
            tail.AddBeforeSelf(new XText(separator.Value), entry);
            return;
        }

        // 这一段里面没有给收尾标签用的换行缩进：刚补出来的空段、手写成 <DataDictionary/> 的、
        // 或者条目挤在同一行的都算。先照着 SecsExpress 空字典的两行写法补一段，再走下面那一步，
        // 否则条目就贴在 <DataDictionary> 后面写成一行，同事那边比对会莫名其妙多出一次改动。
        var closing = dictionary.LastNode as XText;
        if (closing is null || !closing.Value.Contains('\n'))
        {
            dictionary.Add(closing = new XText(LineIndent(dictionary)));
        }

        var closingIndent = closing.Value;
        closing.Value = Deeper(closingIndent);
        closing.AddAfterSelf(entry, new XText(closingIndent));
    }

    /// <summary>
    /// 插在某一条之后。那一条自己行首的换行缩进就是条目这一层的排版，照抄一份跟到它后面。
    /// 抄不到（整份文件挤在一行那种）就只保证内容对、排版挤在一行，
    /// 和结构页里「加同级」的退路一致。
    /// </summary>
    private static void InsertEntryAfter(XElement after, XElement entry)
    {
        if (after.PreviousNode is XText separator && separator.Value.Contains('\n'))
        {
            after.AddAfterSelf(new XText(separator.Value), entry);
            return;
        }

        after.AddAfterSelf(entry);
    }

    /// <summary>
    /// 元素自己在文件里那一行的换行缩进。整份文件挤在一行、前面没有缩进文本时退成光秃秃一个换行，
    /// 内容照样是对的，只是排版没人替它猜。
    /// </summary>
    private static string LineIndent(XElement element)
    {
        if (element.PreviousNode is XText text)
        {
            var cut = text.Value.LastIndexOfAny(['\r', '\n']);
            if (cut >= 0)
            {
                return "\n" + text.Value[(cut + 1)..];
            }
        }

        return "\n";
    }

    /// <summary>把一段换行缩进加深一级：SecsExpress 的 SXML 每级四个空格，所以把行尾那段空白再抄一遍。</summary>
    private static string Deeper(string indent)
    {
        var cut = indent.LastIndexOfAny(['\r', '\n']);
        var pad = indent[(cut + 1)..];
        return pad.Length == 0 ? indent + "    " : indent + pad;
    }

    private void RequireCurrentEntry(DataEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var dictionary = _document.Root?.Element("DataDictionary");
        if (dictionary is null || !ReferenceEquals(entry.Element.Parent, dictionary))
        {
            // DataEntry 是一次性快照：库换过、或者同一条删两次之后，手上的元素已经不在当前文档里了。
            throw new ArgumentException("这个条目不在当前消息库里（库已换过，或者这条已经删掉了）。", nameof(entry));
        }
    }

    private static IReadOnlyList<DataEntry> ParseDataEntries(XDocument document)
    {
        // 整段是空的、或者原来就没有这一段（Load 时已经补上了）都当空列表处理。
        var entries = document.Root?.Element("DataDictionary")?.Elements().ToList();
        if (entries is null)
        {
            return [];
        }

        var parsed = new List<DataEntry>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            parsed.Add(new DataEntry(entries[index], index));
        }

        return parsed;
    }

    private static XDocument CreateEmptyDocument() => new(
        new XElement("SXML",
            // 空的也写成两行，和 SecsExpress 自己的空字典一个写法；一份都没导入过的库
            // 存出来同事也要能打开。
            new XElement("DataDictionary", new XText("\n")),
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

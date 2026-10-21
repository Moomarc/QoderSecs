using System.Xml.Linq;

namespace QoderSecs.Core.Messages;

public sealed record MessageEntry
{
    public MessageEntry(
        XElement element,
        string name,
        string description,
        int stream,
        int function,
        bool wBit,
        string? autoReply,
        XElement? structure,
        int index)
    {
        Element = element;
        Name = name;
        Description = description;
        Stream = stream;
        Function = function;
        WBit = wBit;
        AutoReply = autoReply;
        Structure = structure;
        Index = index;
    }

    public XElement Element { get; }

    public string Name { get; }

    /// <summary>SecsExpress 的 Message Description 属性；文件里没有这个属性时是空串。</summary>
    public string Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public int Stream { get; }

    public int Function { get; }

    public bool WBit { get; }

    public string? AutoReply { get; }

    public XElement? Structure { get; }

    public int Index { get; }

    public string Signature => $"S{Stream}F{Function}";

    /// <summary>
    /// 左栏那一列的记号：置了 W 位写 W，没置写短横。
    /// 放在 Core 是为了让「哪些消息要求回复」这条判断能被测到，界面只做绑定。
    /// </summary>
    public string WBitMark => WBit ? "W" : "-";
}

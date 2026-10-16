using System.Xml.Linq;

namespace QoderSecs.Core.Messages;

public sealed record MessageEntry
{
    public MessageEntry(
        XElement element,
        string name,
        int stream,
        int function,
        bool wBit,
        string? autoReply,
        XElement? structure,
        int index)
    {
        Element = element;
        Name = name;
        Stream = stream;
        Function = function;
        WBit = wBit;
        AutoReply = autoReply;
        Structure = structure;
        Index = index;
    }

    public XElement Element { get; }

    public string Name { get; }

    public int Stream { get; }

    public int Function { get; }

    public bool WBit { get; }

    public string? AutoReply { get; }

    public XElement? Structure { get; }

    public int Index { get; }

    public string Signature => $"S{Stream}F{Function}";
}

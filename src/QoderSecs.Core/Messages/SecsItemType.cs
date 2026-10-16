namespace QoderSecs.Core.Messages;

public enum SecsItemType
{
    List,
    A,
    B,
    Boolean,
    J,
    I1,
    I2,
    I4,
    I8,
    U1,
    U2,
    U4,
    U8,
    F4,
    F8
}

public static class SecsItemTypes
{
    private static readonly Dictionary<string, (SecsItemType Type, string ElementName)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["L"] = (SecsItemType.List, "L"),
        ["A"] = (SecsItemType.A, "A"),
        ["B"] = (SecsItemType.B, "B"),
        ["Boolean"] = (SecsItemType.Boolean, "Boolean"),
        ["J"] = (SecsItemType.J, "J"),
        ["I1"] = (SecsItemType.I1, "I1"),
        ["I2"] = (SecsItemType.I2, "I2"),
        ["I4"] = (SecsItemType.I4, "I4"),
        ["I8"] = (SecsItemType.I8, "I8"),
        ["U1"] = (SecsItemType.U1, "U1"),
        ["U2"] = (SecsItemType.U2, "U2"),
        ["U4"] = (SecsItemType.U4, "U4"),
        ["U8"] = (SecsItemType.U8, "U8"),
        ["F4"] = (SecsItemType.F4, "F4"),
        ["F8"] = (SecsItemType.F8, "F8")
    };

    public static bool TryParse(string name, out SecsItemType type)
    {
        if (Known.TryGetValue(name, out var known))
        {
            type = known.Type;
            return true;
        }

        type = SecsItemType.List;
        return false;
    }

    public static bool TryGetElementName(string name, out string elementName)
    {
        if (Known.TryGetValue(name, out var known))
        {
            elementName = known.ElementName;
            return true;
        }

        elementName = name;
        return false;
    }

    private static readonly Dictionary<SecsItemType, string> NamesByType =
        Known.Values.ToDictionary(known => known.Type, known => known.ElementName);

    /// <summary>类型对应的元素名。从 Known 反过来建，两边不会跑偏。</summary>
    public static string ElementName(SecsItemType type) => NamesByType[type];
}

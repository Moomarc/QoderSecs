using System.Xml.Linq;

namespace QoderSecs.Core.Messages;

/// <summary>
/// 结构树的增删改。直接改 XElement——StructureNode 包的就是 XDocument 里的活节点，
/// 改完 Export 立刻生效，不用重新解析消息库。
///
/// 代价是手上的 StructureNode 在构造时就把 Children 和 Values 拍成了快照，
/// 改完必须重新包一次才看得见新内容。
/// </summary>
public static class StructureEditor
{
    /// <summary>
    /// 建一个新元素。L 是空的；其余类型带一个 &lt;Value&gt;，填的是这个类型必然合法的默认值，
    /// 免得刚插进去的节点就是红的。不写 Maps——真实 SXML 里 243 个 L 都没有 Maps，
    /// SecsExpress 照样打开，所以它是可选的。
    /// </summary>
    public static XElement NewElement(SecsItemType type)
    {
        var element = new XElement(SecsItemTypes.ElementName(type));
        if (type != SecsItemType.List)
        {
            element.Add(new XElement("Value", DefaultValue(type)));
        }

        return element;
    }

    /// <summary>
    /// 加一个子项。parent 是空的 &lt;Structure&gt; 时，加的是这条消息的根元素——
    /// 新消息的结构是空的，没有这个入口就永远建不出树。
    /// 返回 null 表示这个位置不允许加。
    /// </summary>
    public static XElement? AddChild(XElement parent, SecsItemType type)
    {
        if (!CanAddChild(parent))
        {
            return null;
        }

        var added = NewElement(type);
        parent.Add(added);
        return added;
    }

    /// <summary>
    /// 这个位置允不允许加子项。菜单灰不灰和 AddChild 返回 null 与否必须是同一条判断，
    /// 否则界面显示能点、点下去没反应（或反过来）。
    /// </summary>
    public static bool CanAddChild(XElement parent)
    {
        return parent.Name.LocalName == "Structure"
            ? StructureNode.FromStructure(parent) is null
            : IsList(parent);
    }

    /// <summary>
    /// 在同一个 L 里紧跟 element 之后插一个同级。根元素返回 null，因为一条消息只有一个根。
    /// </summary>
    public static XElement? AddSibling(XElement element, SecsItemType type)
    {
        if (!CanAddSibling(element))
        {
            return null;
        }

        var added = NewElement(type);
        element.AddAfterSelf(added);
        return added;
    }

    /// <summary>允不允许在 element 后面加同级。根元素不行，一条消息只有一个根。</summary>
    public static bool CanAddSibling(XElement element)
    {
        var parent = element.Parent;
        return parent is not null && IsList(parent);
    }

    /// <summary>删掉 element。删根元素就是把这条消息的结构清空。</summary>
    public static bool Delete(XElement element)
    {
        if (element.Parent is null)
        {
            return false;
        }

        element.Remove();
        return true;
    }

    /// <summary>改元素的名字。留空就把 Name 属性去掉，和没名字的元素写法一致。</summary>
    public static void SetName(XElement element, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            element.Attribute("Name")?.Remove();
            return;
        }

        element.SetAttributeValue("Name", name);
    }

    /// <summary>
    /// 改单个值。返回 false 表示这个位置没有值可改（L、数组、未知类型），
    /// 界面输入框显不显示和这里必须是同一条判断，见 StructureNode.IsValueEditable。
    /// </summary>
    public static bool SetValue(XElement element, string value)
    {
        if (!new StructureNode(element).IsValueEditable)
        {
            return false;
        }

        var valueElement = element.Element("Value");
        if (valueElement is null)
        {
            // 手写 SXML 可能漏了 <Value>；补的时候放在 Maps 前面，和 SecsExpress 的写法一致。
            var maps = element.Element("Maps");
            valueElement = new XElement("Value");
            if (maps is null)
            {
                element.Add(valueElement);
            }
            else
            {
                maps.AddBeforeSelf(valueElement);
            }
        }

        valueElement.Value = value;
        return true;
    }

    private static string DefaultValue(SecsItemType type) => type switch
    {
        SecsItemType.Boolean => "False",
        SecsItemType.A or SecsItemType.J or SecsItemType.B => string.Empty,
        _ => "0"
    };

    private static bool IsList(XElement element) =>
        SecsItemTypes.TryParse(element.Name.LocalName, out var type) && type == SecsItemType.List;
}

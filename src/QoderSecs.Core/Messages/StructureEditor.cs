using System.Xml.Linq;

namespace QoderSecs.Core.Messages;

/// <summary>
/// 结构树的增删改。直接改 XElement——StructureNode 包的就是 XDocument 里的活节点，
/// 改完 Export 立刻生效，不用重新解析消息库。
///
/// 代价是手上的 StructureNode 在构造时就把 Children 和拆出来的各个值拍成了快照，
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

    /// <summary>
    /// 深拷贝一棵子树，返回的克隆体没有父节点。
    /// 必须深拷贝：浅拷贝会让两份共用同一批子节点实例，改一份连着改掉另一份
    /// （MessageLibrary 复制整条消息时踩的是同一个坑）。
    /// Maps、未知属性、多行 Value 一起带走——它们是保真口径里的内容，不能在这一份上丢。
    /// </summary>
    public static XElement CloneSubtree(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return new XElement(element);
    }

    /// <summary>
    /// 把一份克隆插到 target 上。asChild 为真时成为 target 的子项——target 是空的
    /// &lt;Structure&gt; 时插进去的就是这条消息的根；为假时插在 target 之后成为它的同级。
    /// 落点判断照 CanAddChild / CanAddSibling 那一条，不另写一份，否则又会出现
    /// 「菜单显示能点、点下去没反应」。返回 null 表示这个位置不收。
    /// </summary>
    public static XElement? InsertClone(XElement clone, XElement target, bool asChild)
    {
        ArgumentNullException.ThrowIfNull(clone);
        ArgumentNullException.ThrowIfNull(target);

        if (clone.Parent is not null)
        {
            throw new ArgumentException("要插的必须是克隆体，不能是还挂在树上的活节点——那等于把原节点搬走。", nameof(clone));
        }

        if (asChild)
        {
            if (!CanAddChild(target))
            {
                return null;
            }

            target.Add(clone);
            return clone;
        }

        if (!CanAddSibling(target))
        {
            return null;
        }

        target.AddAfterSelf(clone);
        return clone;
    }

    /// <summary>
    /// 粘贴时这个位置收不收、以什么方式收。先试着落进去，落不进去才看能不能当同级。
    /// 界面里粘贴命令灰不灰和 PasteInto 返回 null 与否必须是同一条判断。
    /// </summary>
    public static PastePlacement PlacementOf(XElement target) =>
        CanAddChild(target) ? PastePlacement.AsChild
        : CanAddSibling(target) ? PastePlacement.AsSibling
        : PastePlacement.Nowhere;

    /// <summary>
    /// 按 <see cref="PlacementOf"/> 给出的落点把克隆插下去。
    /// data 元素不能接收子项，所以落在它上面时走的是同级那一步。
    /// </summary>
    public static XElement? PasteInto(XElement clone, XElement target) => PlacementOf(target) switch
    {
        PastePlacement.AsChild => InsertClone(clone, target, asChild: true),
        PastePlacement.AsSibling => InsertClone(clone, target, asChild: false),
        _ => null
    };

    /// <summary>
    /// 这个节点参不参与拖动。判据就是「它有没有同级位置」：根元素的父是 &lt;Structure&gt;，
    /// &lt;Value&gt; 和 &lt;Maps&gt; 的父不是列表容器，两者都从这里拿到 false——
    /// 就是「根只能被落进，不参与拖动」这一条。
    /// </summary>
    public static bool CanMove(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return CanAddSibling(element);
    }

    /// <summary>
    /// 把 dragged 拖到 target 上会落在哪。粘贴那一条判断照用，前面多拦四种情况：
    /// 不能动的节点（根、Value、Maps）、拖到自己身上、拖进自己的后代里形成自嵌套、
    /// 跨段搬（消息段和字典段之间只开复制，不搬活节点）。
    /// 两个节点必须挂在同一份文档里，不然搬过去就是跨消息改结构，界面那层不该给这条路。
    /// </summary>
    public static PastePlacement DropPlacementOf(XElement dragged, XElement target) =>
        DropPlacementOf(dragged, target, DropZone.Middle);

    /// <summary>
    /// 带落点区域的版本。上段和下段只走同级，目标没有同级位置（根元素）时不收；
    /// 中段照原来的判断，先试子项、再退同级。前面那几道拦截（能不能动、自己、子树、同文档、同段）一视同仁。
    /// </summary>
    public static PastePlacement DropPlacementOf(XElement dragged, XElement target, DropZone zone)
    {
        if (dragged is null || target is null || !CanMove(dragged))
        {
            return PastePlacement.Nowhere;
        }

        if (ReferenceEquals(dragged, target))
        {
            return PastePlacement.Nowhere;
        }

        for (var parent = target.Parent; parent is not null; parent = parent.Parent)
        {
            if (ReferenceEquals(parent, dragged))
            {
                return PastePlacement.Nowhere;
            }
        }

        if (!ReferenceEquals(dragged.Document, target.Document))
        {
            return PastePlacement.Nowhere;
        }

        // 同一段里才让搬。一份消息库的 XML 里两段本来就挂在一起，光查文档分不开它们，
        // 不拦这一步就能把消息结构里的一个活节点直接拖进字典条目里，那条消息跟着少一个节点。
        if (SecsSections.Of(dragged) != SecsSections.Of(target))
        {
            return PastePlacement.Nowhere;
        }

        if (zone == DropZone.Middle)
        {
            return PlacementOf(target);
        }

        if (!CanAddSibling(target))
        {
            return PastePlacement.Nowhere;
        }

        return zone == DropZone.Top ? PastePlacement.AsSiblingBefore : PastePlacement.AsSibling;
    }

    /// <summary>
    /// 把节点搬到 target 上。先摘下来再插，插的才是同一个实例而不是第二份。
    /// 判成复制的那种落点这里也拒：那条路要走 <see cref="CopyForDrop"/>，
    /// 从字典的一个条目里把活节点挖进另一个条目，等于偷偷改掉来源那一条。
    /// 返回 false 表示这个落点不收或者只准复制，树保持原样。
    /// </summary>
    public static bool Move(XElement dragged, XElement target) =>
        Move(dragged, target, DropZone.Middle);

    /// <summary>带落点区域的搬动。上段插到目标前面，下段插到后面，中段照旧。</summary>
    public static bool Move(XElement dragged, XElement target, DropZone zone)
    {
        ArgumentNullException.ThrowIfNull(dragged);
        ArgumentNullException.ThrowIfNull(target);

        var placement = DropPlacementOf(dragged, target, zone);
        if (placement == PastePlacement.Nowhere || DropWayOf(dragged, target, zone) != DropWay.Move)
        {
            return false;
        }

        dragged.Remove();
        switch (placement)
        {
            case PastePlacement.AsChild:
                target.Add(dragged);
                break;
            case PastePlacement.AsSiblingBefore:
                target.AddBeforeSelf(dragged);
                break;
            default:
                target.AddAfterSelf(dragged);
                break;
        }

        return true;
    }

    /// <summary>
    /// 放下去是搬走还是复制一份。收不收照 <see cref="DropPlacementOf"/>，这一段只判怎么办：
    /// 消息那一棵树里一律搬——那本来就是一棵有隶属关系的树。
    /// 字典段按「跨不跨顶层条目」判：条目之间相互不隶属，
    /// 把别的条目里的东西落到这个条目底下，给的是副本，来源那一条一个字都不少；
    /// 同一个条目内部还是搬。顶层条目在字典那一层重排也算搬——落点容器就是它自己待的那一层。
    /// </summary>
    public static DropWay DropWayOf(XElement dragged, XElement target) =>
        DropWayOf(dragged, target, DropZone.Middle);

    /// <summary>带落点区域的那一版；上段和下段的落点层都是目标的父元素，判搬还是判复制照旧。</summary>
    public static DropWay DropWayOf(XElement dragged, XElement target, DropZone zone)
    {
        var placement = DropPlacementOf(dragged, target, zone);
        if (placement == PastePlacement.Nowhere)
        {
            return DropWay.Nowhere;
        }

        if (SecsSections.Of(dragged) != SecsSection.Dictionary)
        {
            return DropWay.Move;
        }

        var landing = placement == PastePlacement.AsChild ? target : target.Parent!;
        return ReferenceEquals(landing, dragged.Parent)
            || ReferenceEquals(TopEntryOf(dragged), TopEntryOf(target))
                ? DropWay.Move
                : DropWay.Copy;
    }

    /// <summary>
    /// 按 <see cref="DropWayOf"/> 判成复制的那个落点，插一份克隆，原节点不动。
    /// 判成移动或拒收的落点返回 null——那两条各有自己的走法，不能在这里悄悄把原节点搬走。
    /// </summary>
    public static XElement? CopyForDrop(XElement dragged, XElement target) =>
        CopyForDrop(dragged, target, DropZone.Middle);

    /// <summary>带落点区域的复制。上段插到目标前面，下段插到后面，中段照旧。</summary>
    public static XElement? CopyForDrop(XElement dragged, XElement target, DropZone zone)
    {
        if (DropWayOf(dragged, target, zone) != DropWay.Copy)
        {
            return null;
        }

        var clone = CloneSubtree(dragged);
        return DropPlacementOf(dragged, target, zone) switch
        {
            PastePlacement.AsChild => InsertClone(clone, target, asChild: true),
            PastePlacement.AsSiblingBefore => InsertCloneBefore(clone, target),
            PastePlacement.AsSibling => InsertClone(clone, target, asChild: false),
            _ => null
        };
    }

    private static XElement? InsertCloneBefore(XElement clone, XElement target)
    {
        if (!CanAddSibling(target))
        {
            return null;
        }

        target.AddBeforeSelf(clone);
        return clone;
    }

    /// <summary>沿祖先走到直接挂在 &lt;DataDictionary&gt; 下面的那一个元素，那就是字典里的顶层条目。</summary>
    private static XElement? TopEntryOf(XElement element)
    {
        for (var node = element; node is not null; node = node.Parent)
        {
            if (node.Parent?.Name.LocalName == "DataDictionary")
            {
                return node;
            }
        }

        return null;
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
    /// 改值。数组写成同一个 &lt;Value&gt; 里空格分隔的多个值（照 SecsExpress 的 arrays.sxml），
    /// 所以这里写的始终是整段文本。返回 false 表示这个位置没有值可改（L、未知类型），
    /// 界面输入框显不显示和这里必须是同一条判断，见 StructureNode.IsValueEditable。
    /// </summary>
    public static bool SetValue(XElement element, string value)
    {
        var node = new StructureNode(element);
        if (!node.IsValueEditable)
        {
            return false;
        }

        var values = element.Elements("Value").ToList();
        var valueElement = values.FirstOrDefault();
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

        // 读入时容忍多个 <Value>（拼成一段文本看），改完只能留一个，
        // 否则界面上改一次就把第二种形状留下一份，同事那边读回去的样子和这里显示的对不上。
        foreach (var extra in values.Skip(1))
        {
            extra.Remove();
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

    /// <summary>
    /// 能装子项的容器。除了 SECS-II 的 L，还认 <c>&lt;DataDictionary&gt;</c>：
    /// 字典里那一个个条目就是靠这一行继承功能三那套落点判断的——收得下粘贴、能加同级、能落进。
    /// 消息结构里不会出现这个元素名，所以这一行对消息树没有影响。
    /// </summary>
    private static bool IsList(XElement element) =>
        element.Name.LocalName == "DataDictionary"
        || (SecsItemTypes.TryParse(element.Name.LocalName, out var type) && type == SecsItemType.List);
}

using System.Xml.Linq;

namespace QoderSecs.Core.Messages;

/// <summary>
/// <see cref="MessageLibrary"/> 里 <c>DataDictionary</c> 的一个条目，和 <see cref="MessageEntry"/> 一样
/// 是一次性快照：改过字典之后手上这份的 Element 可能已经不在文档里了，要重新取。
///
/// 条目就是一棵带 Name 的完整 SECS-II 子树（参考件 <c>base-1.sxml</c> 里
/// <c>L Name="sample list"</c> 下挂 A/U4/B/L，就是这个形状），所以内部结构直接交给
/// <see cref="StructureNode"/> 去读，不在这里再包一套模型。
/// </summary>
public sealed record DataEntry
{
    private const int PreviewCharacterCap = 60;

    public DataEntry(XElement element, int index)
    {
        Element = element;
        Index = index;
        Node = new StructureNode(element);
    }

    public XElement Element { get; }

    public StructureNode Node { get; }

    public int Index { get; }

    /// <summary>
    /// 条目的 Name 属性，可以没有、也可以重名（和消息名那条规矩相反，消息名必须唯一，
    /// 自动回复按名字找条目）。认一条条目靠的是它在字典里的位置，不是这个名字。
    /// </summary>
    public string Name => Node.Name ?? string.Empty;

    /// <summary>类型标签就是元素名（L、A、U4、B……），和结构树那一列同一个口径。</summary>
    public string TypeLabel => Node.ElementName;

    /// <summary>
    /// 列表上那一栏预览：有子项报子项数，否则报第一个值。折成一排、超长截断——
    /// 配方正文那种多行 A 原样进列表会把行高撑开，这一栏只是给人认条目用的，
    /// 值本身存在文档里，导出时一个字都不动。
    /// </summary>
    public string Preview
    {
        get
        {
            var text = Node.Children.Count > 0 ? $"{Node.Children.Count} 项" : Node.SingleValue;
            var flat = string.Join(' ', text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            return flat.Length <= PreviewCharacterCap ? flat : string.Concat(flat[..PreviewCharacterCap], "…");
        }
    }

    /// <summary>这一行在读屏和 UI 自动化里读出来的名字，和结构树那一条规矩一样要给显式的。</summary>
    public string AutomationName => $"{Name} {TypeLabel} {Preview}";
}

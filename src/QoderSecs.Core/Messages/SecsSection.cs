using System.Xml.Linq;

namespace QoderSecs.Core.Messages;

/// <summary>一个元素挂在 SXML 的哪一段里。</summary>
public enum SecsSection
{
    /// <summary>某条消息的结构里，也就是 <c>&lt;MessageDictionary&gt;</c> 下面。</summary>
    Message,

    /// <summary>数据字典里，也就是 <c>&lt;DataDictionary&gt;</c> 下面。</summary>
    Dictionary,

    /// <summary>两处都不是：脱离文档的克隆，或者手写文件里挂错了地方的元素。</summary>
    Elsewhere,
}

public static class SecsSections
{
    /// <summary>
    /// 沿祖先往上走，先碰到哪一段就算哪一段。
    /// 一条判断三个地方共用：两棵树的节点登记要分家、跨段不许搬活节点、「插入」那类命令
    /// 只认消息段的选中节点。写成第二处判断迟早会和第一处对不上，界面就会出现
    /// 「菜单显示能点、点下去没反应」。
    /// </summary>
    public static SecsSection Of(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        for (var node = element; node is not null; node = node.Parent)
        {
            if (node.Name.LocalName == "MessageDictionary")
            {
                return SecsSection.Message;
            }

            if (node.Name.LocalName == "DataDictionary")
            {
                return SecsSection.Dictionary;
            }
        }

        return SecsSection.Elsewhere;
    }
}

using System.Xml.Linq;
using QoderSecs.Core.Messages;
using Secs4Net;

namespace QoderSecs.Tests;

/// <summary>
/// 上下图（S12 系列）：产线上马上要用，而且要求稳定传输，所以单独一组测试盯住。
/// 素材是一段 7482 字符的 ASCII 地图正文，外面套 <c>&lt;L [4]&gt;</c>（批号、晶圆号、坐标对、地图）。
/// 地图是合成的（同样的 86×87 圆片、同一套 '.'/4/M/X/n 字符），批号换成了 MASKEDLOT1，
/// 里面不含任何真实批次或机台数据。
/// </summary>
[TestClass]
public sealed class WaferMapTests
{
    private const int MapLength = 7482;

    private static string TestData(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    private static MessageLibrary Library() => MessageLibrary.Load(TestData("wafer-map.sxml"));

    private static string MapBody(MessageLibrary library) => BodyOf(library.Messages[0]);

    private static string BodyOf(MessageEntry entry) => entry.Structure!
        .Descendants("A")
        .Single(element => (string?)element.Attribute("Name") == "WAFER_MAP")
        .Element("Value")!.Value;

    [TestMethod]
    public void TheSampleMapIsTheDocumentedSize()
    {
        var body = MapBody(Library());

        Assert.AreEqual(MapLength, body.Length);
        CollectionAssert.AreEqual(
            new[] { '.', '4', 'M', 'X', 'n' },
            body.Distinct().OrderBy(character => character).ToList(),
            "地图正文只用 E4 那套格子字符，别的名额说明素材被改过。");
    }

    /// <summary>
    /// 比对用的归一化：SML 里没有元素名，也没有 XML 那些换行缩进，所以把两棵树的 Name 属性
    /// 和纯空白文本都去掉。剩下的类型、值、Maps 必须一项不差——那才是要上线的东西。
    /// </summary>
    private static XElement Normalized(XElement element)
    {
        var clone = new XElement(element);
        foreach (var node in clone.DescendantsAndSelf().OfType<XElement>().ToList())
        {
            node.Attribute("Name")?.Remove();
        }

        foreach (var text in clone.DescendantNodesAndSelf().OfType<XText>()
                     .Where(text => !text.Value.Any(character => !char.IsWhiteSpace(character))).ToList())
        {
            text.Remove();
        }

        return clone;
    }

    /// <summary>文档里那条 SML 粘进来，必须落成和库里面一模一样的结构。</summary>
    [TestMethod]
    public void TheDocumentedSmlParsesIntoTheSameStructureAsTheLibrary()
    {
        Assert.IsTrue(SmlParser.TryParse(TestData("wafer-map-s12f9.sml"), out var parsed, out var error), error);

        Assert.AreEqual(12, parsed!.Stream);
        Assert.AreEqual(9, parsed.Function);
        Assert.IsTrue(parsed.ReplyExpected);
        Assert.IsTrue(
            XNode.DeepEquals(Normalized(parsed.Structure), Normalized(Library().Messages[0].Structure!)),
            "粘贴解析出来的结构和库里的对不上。");
    }

    /// <summary>
    /// 上线的那一段必须是 7482 个字节。A 的 [个数] 写的是字节数不是元素个数，
    /// 这一点错了，对面收到的就是一张被截断的图。
    /// </summary>
    [TestMethod]
    public void TheMapGoesOnTheWireAsOneAsciiItemOfItsOwnLength()
    {
        var library = Library();
        Assert.IsTrue(SmlConverter.TryToMessage(library.Messages[0], out var message, out var error), error);

        using (message)
        {
            var items = message.SecsItem!.Items;
            Assert.HasCount(4, items);
            Assert.AreEqual(SecsFormat.ASCII, items[3].Format);
            Assert.AreEqual(MapLength, items[3].GetString()!.Length);
            Assert.AreEqual(MapBody(library), items[3].GetString());
        }
    }

    /// <summary>转成 SML 再粘回来还得是同一棵树：预览、日志、重新导入走的是这一条路。</summary>
    [TestMethod]
    public void TheWholeMessageSurvivesSmlOutAndBackIn()
    {
        var library = Library();
        Assert.IsTrue(SmlConverter.TryToSml(library.Messages[0], out var sml, out var error), error);

        // 坐标那一对上线是两个 I2，不是一个写成 "0 0" 的值；A 的 [个数] 是字节数。
        StringAssert.Contains(sml, "<I2 [2] 0 0>");
        StringAssert.Contains(sml, "'S12F9'W");
        StringAssert.Contains(sml, $"<A [{MapLength}] ");
        Assert.IsTrue(SmlParser.TryParse(sml, out var parsed, out error), error);

        Assert.IsTrue(
            XNode.DeepEquals(Normalized(parsed!.Structure), Normalized(library.Messages[0].Structure!)));
    }

    /// <summary>改名、自动回复指向的目标也得撑得住这张图，不能一到 Secondary 就发不出去。</summary>
    [TestMethod]
    public void TheConfiguredReplyCarriesTheSameMap()
    {
        var library = Library();
        var target = AutoReplyResolver.FindTarget(library, 12, 9);

        Assert.AreEqual("WaferMapAck", target?.Name);
        Assert.IsTrue(SmlConverter.TryToMessage(target!, replyExpected: false, out var reply, out var error), error);

        using (reply)
        {
            Assert.IsFalse(reply!.ReplyExpected);
            Assert.AreEqual(MapBody(library), reply!.SecsItem!.Items[3].GetString());
        }
    }
}

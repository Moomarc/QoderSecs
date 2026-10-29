using System.Xml.Linq;
using QoderSecs.Core.Messages;

namespace QoderSecs.Tests;

/// <summary>
/// 随程序发的常用命令库。它是手工验收和演示的起点，发不出去或者自动回复指错，
/// 在测试里发现总比在界面上发现好。
/// </summary>
[TestClass]
public sealed class SampleLibraryTests
{
    [TestMethod]
    public void EveryMessageIsSendable()
    {
        foreach (var entry in Sample().Messages)
        {
            Assert.IsTrue(SmlConverter.TryToSml(entry, out _, out var error), $"{entry.Name}：{error}");
        }
    }

    [TestMethod]
    public void EveryConfiguredAutoReplyPointsAtARealMessage()
    {
        var names = Sample().Messages.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var entry in Sample().Messages.Where(entry => !string.IsNullOrWhiteSpace(entry.AutoReply)))
        {
            Assert.Contains(
                entry.AutoReply!.Trim(),
                names,
                $"{entry.Name} 的自动回复指向不存在的消息「{entry.AutoReply}」。");
        }
    }

    /// <summary>手工验收就是发这两条，指错了等于白测一轮。</summary>
    [TestMethod]
    public void TheAcceptanceMessagesResolveToTheirAcks()
    {
        var library = Sample();

        Assert.AreEqual("OnlineData", AutoReplyResolver.FindTarget(library, 1, 1)?.Name);
        Assert.AreEqual("EstablishCommsAck", AutoReplyResolver.FindTarget(library, 1, 13)?.Name);
    }

    /// <summary>
    /// 应答消息的正文不能省：S1F18 的 ONLACK、S2F32 的 TIACK 是对方判断结果的唯一依据，
    /// 空着发出去对面解析不到应答码。这两条之前都被写成了「不带正文」。
    /// </summary>
    [TestMethod]
    public void AcknowledgementMessagesCarryTheirStandardBodies()
    {
        var library = Sample();

        AssertAckBody(library, 1, 18, "ONLACK");
        AssertAckBody(library, 2, 32, "TIACK");
        AssertAckBody(library, 2, 34, "DRACK");
        AssertAckBody(library, 2, 36, "LRACK");
        AssertAckBody(library, 2, 38, "ERACK");
        AssertAckBody(library, 7, 4, "PPACK");
        AssertAckBody(library, 7, 18, "PPACK");
        AssertAckBody(library, 10, 4, "ACKC10");
        AssertAckBody(library, 12, 10, "ACK");
    }

    /// <summary>带 W 位的请求都要配 AutoReply，否则联调时对面只能等到 T3。</summary>
    [TestMethod]
    public void EveryWBitRequestHasAnAutoReply()
    {
        foreach (var entry in Sample().Messages.Where(entry => entry.WBit))
        {
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(entry.AutoReply),
                $"{entry.Name} 带 W 位却没有 AutoReply。");
        }
    }

    [TestMethod]
    public void ExportRoundTripsWithoutLosingMessages()
    {
        var library = Sample();
        var reloaded = MessageLibrary.Load(library.Export());

        CollectionAssert.AreEqual(
            library.Messages.Select(entry => entry.Name).ToList(),
            reloaded.Messages.Select(entry => entry.Name).ToList());
    }

    /// <summary>
    /// 样例库不再自带数据字典：CEID、SVID 这些编号每台机都不一样，预置进去等于替用户做了假设，
    /// 用的时候在字典页自己加。
    /// </summary>
    [TestMethod]
    public void CarriesNoDataDictionaryEntries()
    {
        Assert.IsEmpty(Sample().DataEntries, "样例库不该预置数据字典条目。");
    }

    /// <summary>
    /// 条目可以没有，但 <c>&lt;DataDictionary&gt;</c> 这一段不能没有：SecsExpress 见到缺这一段的文件
    /// 直接打不开（实测过）。样例库是手工改的文本文件，所以盯住原文，别哪天被删掉。
    /// </summary>
    [TestMethod]
    public void TheFileCarriesTheSectionSecsExpressRequires()
    {
        var text = ReadSample();

        StringAssert.Contains(text, "<DataDictionary>");
        var root = XDocument.Parse(text, LoadOptions.PreserveWhitespace).Root!;
        Assert.AreEqual("DataDictionary", root.Elements().First().Name.LocalName, "字典段得在消息段之前。");
        Assert.IsFalse(root.Element("DataDictionary")!.IsEmpty, "按 SecsExpress 的写法是两行，不是自闭合。");
        Assert.AreEqual(
            Flat(text),
            Flat(Sample().Export()),
            "样例库读进再导出应该一个字不变——同事比对文件时不该看到一屏假改动。");
    }

    /// <summary>比对排版前先统一两种写法：自闭合的空元素和行尾，理由同 MessageLibraryTests.Normalize。</summary>
    private static string Flat(string xml) =>
        xml.Replace(" />", "/>").Replace("\r\n", "\n").TrimEnd('\n');

    /// <summary>
    /// 这份库是按「留结构、不填数据」的口径给的：叶子节点的 &lt;Value&gt; 一律空着，
    /// 编号和取值由用的人按手上那台机填。界面靠类型名渲染每一项，
    /// 认不出来的名字会显示成「未知元素类型」，所以整棵子树都得是已知类型。
    /// </summary>
    [TestMethod]
    public void StructuresAreKnownTypesAndCarryNoValues()
    {
        foreach (var entry in Sample().Messages)
        {
            foreach (var element in (entry.Structure ?? new XElement("L")).Descendants()
                         .Where(element => element.Name != "Value" && element.Name != "Maps"))
            {
                Assert.IsTrue(
                    SecsItemTypes.TryParse(element.Name.LocalName, out var type),
                    $"{entry.Name} 里出现了认不出的元素 <{element.Name}>。");

                if (type == SecsItemType.List)
                {
                    continue;
                }

                Assert.IsTrue(
                    string.IsNullOrWhiteSpace(element.Element("Value")?.Value),
                    $"{entry.Name} 的 <{element.Name} {element.Attribute("Name")}> 还带着填好的数据。");
            }
        }
    }

    /// <summary>
    /// 消息清单是用户定的：GEM 常用命令留全，并补上上下线和日期时间那几条。
    /// 改动这份清单会先在这里撞到，改的人得是有意为之。
    /// </summary>
    [TestMethod]
    public void MessageSetIsTheAgreedOne()
    {
        var actual = Sample().Messages
            .Select(entry => entry.Signature)
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToList();

        CollectionAssert.AreEqual(
            new[]
            {
                "S10F3", "S10F4", "S1F1", "S1F11", "S1F12", "S1F13", "S1F14", "S1F15",
                "S1F16", "S1F17", "S1F18", "S1F2", "S1F3", "S1F4",
                "S2F31", "S2F32", "S2F33", "S2F34", "S2F35", "S2F36", "S2F37", "S2F38",
                "S2F41", "S2F42",
                "S5F1", "S5F2", "S6F11", "S6F12",
                "S7F17", "S7F18", "S7F19", "S7F20", "S7F3", "S7F4", "S7F5", "S7F6",
                "S12F10", "S12F15", "S12F16", "S12F9",
            }.OrderBy(signature => signature, StringComparer.Ordinal).ToList(),
            actual);
    }

    /// <summary>消息名只写用途，SxFy 由 Stream/Function 字段表示，不再塞进名字前缀。</summary>
    [TestMethod]
    public void MessageNamesDoNotRepeatTheirSignature()
    {
        foreach (var entry in Sample().Messages)
        {
            Assert.IsFalse(
                entry.Name.StartsWith(entry.Signature + "_", StringComparison.OrdinalIgnoreCase),
                $"{entry.Name} 还把 SxFy 写在名字前缀里。");
        }
    }

    private static void AssertAckBody(MessageLibrary library, int stream, int function, string name)
    {
        var entry = library.Messages.Single(message => message.Stream == stream && message.Function == function);
        var body = entry.Structure?.Elements().SingleOrDefault();
        Assert.IsNotNull(body, $"{entry.Name} 的消息体是空的，应该带 {name}。");
        Assert.AreEqual("B", body!.Name.LocalName, $"{entry.Name} 的 {name} 应该是二进制元素。");
        Assert.AreEqual(
            name,
            body.Attribute("Name")?.Value,
            $"{entry.Name} 的应答码节点应该叫 {name}。");
    }

    private static string ReadSample() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "rich-set.sxml"));

    private static MessageLibrary Sample() => MessageLibrary.Load(ReadSample());
}

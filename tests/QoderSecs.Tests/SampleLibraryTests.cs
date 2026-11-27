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
    /// 应答消息的正文不能省：S2F32 的 TIACK、S12F6 的 GRNT1 是对方判断结果的唯一依据，
    /// 空着发出去对面解析不到应答码。这几条之前都被写成了「不带正文」。
    /// </summary>
    [TestMethod]
    public void AcknowledgementMessagesCarryTheirStandardBodies()
    {
        var library = Sample();

        AssertAckBody(library, 2, 32, "TIACK");
        AssertAckBody(library, 2, 34, "DRACK");
        AssertAckBody(library, 2, 36, "LRACK");
        AssertAckBody(library, 2, 38, "ERACK");
        AssertAckBody(library, 5, 2, "ACKC5");
        AssertAckBody(library, 6, 12, "ACKC6");
        AssertAckBody(library, 7, 4, "ACKC7");
        AssertAckBody(library, 7, 18, "ACKC7");
        AssertAckBody(library, 10, 4, "ACKC10");
        AssertAckBody(library, 12, 6, "GRNT1");
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
    /// 界面靠类型名渲染每一项，认不出来的名字会显示成「未知元素类型」，所以整棵子树都得是已知类型。
    /// </summary>
    [TestMethod]
    public void StructuresUseOnlyKnownTypes()
    {
        foreach (var entry in Sample().Messages)
        {
            foreach (var element in (entry.Structure ?? new XElement("L")).Descendants()
                         .Where(element => element.Name != "Value" && element.Name != "Maps"))
            {
                Assert.IsTrue(
                    SecsItemTypes.TryParse(element.Name.LocalName, out _),
                    $"{entry.Name} 里出现了认不出的元素 <{element.Name}>。");
            }
        }
    }

    /// <summary>
    /// 两份库都填成即用，导入后只改编号不改结构。
    /// 应答码一律 00（「接受」），空着发出去对面解析不到结果。
    /// </summary>
    [TestMethod]
    public void EveryAcknowledgementCodeDefaultsToAccept()
    {
        string[] codes =
        [
            "COMMACK", "TIACK", "DRACK", "LRACK", "ERACK", "HCACK", "CPACK",
            "ACKC5", "ACKC6", "ACKC7", "ACKC10", "GRNT1",
        ];

        foreach (var entry in Sample().Messages)
        {
            foreach (var element in (entry.Structure ?? new XElement("L")).Descendants()
                         .Where(element => codes.Contains(element.Attribute("Name")?.Value)))
            {
                Assert.AreEqual(
                    "00",
                    element.Element("Value")?.Value,
                    $"{entry.Name} 的 {element.Attribute("Name")} 应该默认填 00。");
            }
        }
    }

    /// <summary>S2F41 拆成三条命令，各自的应答也拆开；RCMD 和参数都填好，导入即可发。</summary>
    [TestMethod]
    public void HostCommandIsSplitIntoThreeReadyToSendCommands()
    {
        var library = Sample();
        var commands = library.Messages.Where(entry => entry is { Stream: 2, Function: 41 }).ToList();

        CollectionAssert.AreEqual(
            new[] { "START", "STOP", "PP_SELECT" },
            commands.Select(entry => Value(entry, "RCMD")).ToList());

        foreach (var command in commands)
        {
            var ack = library.Messages.Single(entry => entry.Name == command.AutoReply);
            Assert.AreEqual("S2F42", ack.Signature);
            Assert.AreEqual("00", Value(ack, "HCACK"));
        }

        var select = commands.Single(entry => Value(entry, "RCMD") == "PP_SELECT");
        Assert.AreEqual("PPID", Value(select, "CPNAME"));
        Assert.AreEqual("RECIPE01", Value(select, "CPVAL"));
    }

    /// <summary>
    /// 这几处是用户点名的类型错误和 E5 核对出来的错名：PPBODY 常用二进制不是 ASCII，
    /// CEED 是 Boolean，S7F4/S7F18 的应答码叫 ACKC7（PPACK 不是 E5 的数据项），
    /// S12 用 MID/IDTYP/STRP/BINLT，不是自造的 LOTID/WAFERID/MAP_XY/WAFER_MAP。
    /// </summary>
    [TestMethod]
    public void FieldNamesAndTypesMatchTheStandard()
    {
        var library = Sample();

        AssertType(library, "ProcessProgramSend", "PPBODY", "B");
        AssertType(library, "ProcessProgramData", "PPBODY", "B");
        AssertType(library, "EnableDisableEvents", "CEED", "Boolean");
        Assert.AreEqual("True", Value(library.Messages.Single(e => e.Name == "EnableDisableEvents"), "CEED"));

        foreach (var name in new[] { "MapTransmitInquire", "MapDataRequest", "MapData" })
        {
            var entry = library.Messages.Single(e => e.Name == name);
            Assert.IsNotNull(Find(entry, "MID"), $"{name} 缺 MID。");
            Assert.IsNotNull(Find(entry, "IDTYP"), $"{name} 缺 IDTYP。");
        }

        var mapData = library.Messages.Single(e => e.Name == "MapData");
        AssertType(library, "MapData", "STRP", "I2");
        AssertType(library, "MapData", "BINLT", "U1");
        Assert.AreEqual("0 0", Value(mapData, "STRP"));

        foreach (var name in new[] { "GetAttrRequest", "GetAttrData" })
        {
            var entry = library.Messages.Single(e => e.Name == name);
            Assert.IsNotNull(Find(entry, "OBJID"), $"{name} 缺 OBJID。");
            Assert.IsNotNull(Find(entry, "ATTRID"), $"{name} 缺 ATTRID。");
        }

        Assert.AreEqual("StripMap", Value(library.Messages.Single(e => e.Name == "GetAttrRequest"), "OBJTYPE"));
        Assert.AreEqual("0", Value(library.Messages.Single(e => e.Name == "GetAttrData"), "OBJACK"));
    }

    /// <summary>
    /// 无正文和 L:0 在 SECS-II 上是两回事，别混：S1F1/S7F19 只有报文头，
    /// S1F13 的 Host 格式是空列表，S1F14 的 Host 格式是 L2{COMMACK, L0}。
    /// </summary>
    [TestMethod]
    public void HeaderOnlyAndEmptyListMessagesAreNotConfused()
    {
        var library = Sample();

        foreach (var name in new[] { "AreYouThere", "CurrentEPPDRequest" })
        {
            var entry = library.Messages.Single(e => e.Name == name);
            Assert.IsTrue(
                entry.Structure is null || !entry.Structure.Elements().Any(),
                $"{name} 只有报文头，不该带正文。");
        }

        var establish = library.Messages.Single(e => e.Name == "EstablishComms");
        var establishBody = establish.Structure!.Elements().Single();
        Assert.AreEqual("L", establishBody.Name.LocalName);
        Assert.IsEmpty(establishBody.Elements(), "S1F13 的 Host 格式是 L:0。");

        var ack = library.Messages.Single(e => e.Name == "EstablishCommsAck");
        var ackBody = ack.Structure!.Elements().Single();
        Assert.HasCount(2, ackBody.Elements());
        Assert.AreEqual("COMMACK", ackBody.Elements().First().Attribute("Name")?.Value);
        Assert.IsEmpty(ackBody.Elements().Last().Elements(), "S1F14 的 Host 格式第二项是 L:0。");
    }

    /// <summary>
    /// 消息清单是用户定的：删掉不常用的 S1F15–18，S2F41/42 各拆三条命令，
    /// S12 只留 F5/6/15/16，补 S14F1/2 传 StripMap。改这份清单会先在这里撞到。
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
                "S1F1", "S1F2", "S1F3", "S1F4", "S1F11", "S1F12", "S1F13", "S1F14",
                "S2F31", "S2F32", "S2F33", "S2F34", "S2F35", "S2F36", "S2F37", "S2F38",
                "S2F41", "S2F41", "S2F41", "S2F42", "S2F42", "S2F42",
                "S5F1", "S5F2", "S6F11", "S6F12",
                "S7F3", "S7F4", "S7F5", "S7F6", "S7F17", "S7F18", "S7F19", "S7F20",
                "S10F3", "S10F4",
                "S12F5", "S12F6", "S12F15", "S12F16",
                "S14F1", "S14F2",
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

    private static XElement? Find(MessageEntry entry, string name) =>
        entry.Structure?.Descendants().FirstOrDefault(node => node.Attribute("Name")?.Value == name);

    private static string? Value(MessageEntry entry, string name) =>
        Find(entry, name)?.Element("Value")?.Value;

    private static void AssertType(MessageLibrary library, string message, string field, string type)
    {
        var entry = library.Messages.Single(e => e.Name == message);
        Assert.AreEqual(
            type,
            Find(entry, field)?.Name.LocalName,
            $"{message} 的 {field} 应该是 {type}。");
    }

    private static string ReadSample() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "rich-set.sxml"));

    private static MessageLibrary Sample() => MessageLibrary.Load(ReadSample());
}

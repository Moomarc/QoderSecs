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

        Assert.AreEqual("S1F2_OnlineData", AutoReplyResolver.FindTarget(library, 1, 1)?.Name);
        Assert.AreEqual("S1F14_EstablishCommsAck", AutoReplyResolver.FindTarget(library, 1, 13)?.Name);
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
    /// 消息清单是用户定的：GEM 常用命令留全，日期时间和上下线那几条不要。
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
                "S10F3", "S1F1", "S1F11", "S1F13", "S1F14", "S1F2", "S1F3", "S1F4",
                "S2F33", "S2F35", "S2F37", "S2F41", "S2F42",
                "S5F1", "S5F2", "S6F11", "S6F12", "S7F17", "S7F19", "S7F3", "S7F5",
                "S12F9", "S12F16",
            }.OrderBy(signature => signature, StringComparer.Ordinal).ToList(),
            actual);
    }

    private static string ReadSample() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "common-commands.sxml"));

    private static MessageLibrary Sample() => MessageLibrary.Load(ReadSample());
}

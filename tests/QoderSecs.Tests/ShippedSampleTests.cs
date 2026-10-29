using System.IO;
using System.Xml.Linq;
using QoderSecs.Core.Messages;
using QoderSecs.Core.Profiles;

namespace QoderSecs.Tests;

/// <summary>
/// 随 exe 解出去的两份样本要过的几条。盯的是「同事拿到 exe 第一眼导入的东西能不能用」：
/// SecsExpress 打不开样本，等于发布件是坏的——上一版就是这么翻车的，
/// 真因是缺 <c>&lt;DataDictionary&gt;</c> 那一段，单变量对照件的实测结论记在 MessageLibrary 里。
///
/// rich-set 是骨架，数值一律空着（「留结构不填数据」的口径由 SampleLibraryTests 盯）；
/// raw-set 只有最常见的五对（S1F1/2、S1F13/14、S2F41/42、S5F1/2、S6F11/12），
/// 变量带默认值，导入就能直接发。
/// S12 上下图那套带正文的素材留在 TestData/wafer-map.sxml，由 WaferMapTests 和回环测试盯，
/// 不再随 exe 发出去。
/// </summary>
[TestClass]
public sealed class ShippedSampleTests
{
    private sealed record Shipped(string FileName, string Text, MessageLibrary Library);

    /// <summary>
    /// 取一份随 exe 发的样本。文件名同时是启动时的解包名和测试工程链接过来的名字，
    /// 改一处要改两处。
    /// </summary>
    private static Shipped Sample(string fileName)
    {
        var text = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "TestData", fileName));
        return new Shipped(fileName, text, MessageLibrary.Load(text));
    }

    private static IReadOnlyList<Shipped> Samples() =>
        AppDataPaths.SampleFileNames.Select(Sample).ToList();

    [TestMethod]
    public void BothSamplesCarryTheSectionSecsExpressRequires()
    {
        foreach (var sample in Samples())
        {
            StringAssert.Contains(
                sample.Text,
                "<DataDictionary>",
                $"{sample.FileName} 缺了 <DataDictionary> 段，SecsExpress 打不开。");

            var root = XDocument.Parse(sample.Text, LoadOptions.PreserveWhitespace).Root!;
            Assert.AreEqual(
                "DataDictionary",
                root.Elements().First().Name.LocalName,
                $"{sample.FileName} 的字典段得排在消息段之前。");
            Assert.IsFalse(
                root.Element("DataDictionary")!.IsEmpty,
                $"{sample.FileName} 的字典段按 SecsExpress 的写法是两行，不能塌成自闭合。");
        }
    }

    [TestMethod]
    public void BothSamplesRoundTripWithoutLosingAnything()
    {
        foreach (var sample in Samples())
        {
            Assert.AreEqual(
                Flat(sample.Text),
                Flat(sample.Library.Export()),
                $"{sample.FileName} 读进再导出应该一个字不变——同事比对文件时不该看到一屏假改动。");
        }
    }

    [TestMethod]
    public void EveryMessageInBothSamplesIsSendable()
    {
        foreach (var sample in Samples())
        {
            foreach (var entry in sample.Library.Messages)
            {
                Assert.IsTrue(
                    SmlConverter.TryToSml(entry, out _, out var error),
                    $"{sample.FileName} 的 {entry.Name}：{error}");
            }
        }
    }

    [TestMethod]
    public void EveryAutoReplyInBothSamplesPointsAtARealMessage()
    {
        foreach (var sample in Samples())
        {
            var names = sample.Library.Messages.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var entry in sample.Library.Messages
                         .Where(entry => !string.IsNullOrWhiteSpace(entry.AutoReply)))
            {
                Assert.Contains(
                    entry.AutoReply!.Trim(),
                    names,
                    $"{sample.FileName} 的 {entry.Name} 指向不存在的消息「{entry.AutoReply}」。");
            }
        }
    }

    [TestMethod]
    public void TheRawSampleIsTheAgreedFivePairs()
    {
        var raw = Sample(AppDataPaths.RawSampleFileName);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "S1F1", "S1F2", "S1F13", "S1F14",
                "S2F41", "S2F42", "S5F1", "S5F2", "S6F11", "S6F12",
            },
            raw.Library.Messages.Select(entry => entry.Signature).ToList(),
            "raw-set 只该有这十条。");

        Assert.AreEqual("OnlineData", AutoReplyResolver.FindTarget(raw.Library, 1, 1)?.Name);
        Assert.AreEqual("EstablishCommsAck", AutoReplyResolver.FindTarget(raw.Library, 1, 13)?.Name);
        Assert.AreEqual("HostCommandAck", AutoReplyResolver.FindTarget(raw.Library, 2, 41)?.Name);
        Assert.AreEqual("AlarmReportAck", AutoReplyResolver.FindTarget(raw.Library, 5, 1)?.Name);
        Assert.AreEqual("EventReportAck", AutoReplyResolver.FindTarget(raw.Library, 6, 11)?.Name);
    }

    /// <summary>raw-set 和 rich-set 的唯一区别就在这儿：变量带着能直接发出去的默认值。</summary>
    [TestMethod]
    public void TheRawSampleCarriesDefaultValues()
    {
        var raw = Sample(AppDataPaths.RawSampleFileName);

        AssertValue(raw.Library, 1, 2, "MDLN", "QODERSECS");
        AssertValue(raw.Library, 1, 2, "SOFTREV", "1.0");
        AssertValue(raw.Library, 1, 14, "COMMACK", "00");
        AssertValue(raw.Library, 2, 41, "RCMD", "START");
        AssertValue(raw.Library, 2, 42, "HCACK", "00");
        AssertValue(raw.Library, 5, 1, "ALCD", "81");
        AssertValue(raw.Library, 5, 1, "ALID", "1");
        AssertValue(raw.Library, 5, 1, "ALTX", "ALARM");
        AssertValue(raw.Library, 5, 2, "ACK", "00");
        AssertValue(raw.Library, 6, 11, "DATAID", "0");
        AssertValue(raw.Library, 6, 11, "CEID", "1");
        AssertValue(raw.Library, 6, 11, "RPTID", "1");
        AssertValue(raw.Library, 6, 12, "ACK", "00");
    }

    private static void AssertValue(
        MessageLibrary library,
        int stream,
        int function,
        string name,
        string expected)
    {
        var entry = library.Messages.Single(message => message.Stream == stream && message.Function == function);
        var element = entry.Structure!.Descendants()
            .Single(node => node.Attribute("Name")?.Value == name);

        Assert.AreEqual(
            expected,
            element.Element("Value")?.Value,
            $"{entry.Signature} 的 {name} 默认值不对。");
    }

    /// <summary>比对排版前先统一两种写法：自闭合的空元素和行尾，理由同 MessageLibraryTests.Normalize。</summary>
    private static string Flat(string xml) =>
        xml.Replace(" />", "/>").Replace("\r\n", "\n").TrimEnd('\n');
}

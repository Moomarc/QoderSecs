using System.IO;
using System.Xml.Linq;
using QoderSecs.Core.Messages;
using QoderSecs.Core.Profiles;

namespace QoderSecs.Tests;

/// <summary>
/// 随 exe 解出去的那一份样本要过的几条。盯的是「同事拿到 exe 第一眼导入的东西能不能用」：
/// SecsExpress 打不开样本，等于发布件是坏的——上一版就是这么翻车的，
/// 真因是缺 <c>&lt;DataDictionary&gt;</c> 那一段，单变量对照件的实测结论记在 MessageLibrary 里。
///
/// 「留结构不填数据」「消息清单」那些口径由 SampleLibraryTests 盯；
/// S12 上下图那套带正文的素材留在 TestData/wafer-map.sxml，由 WaferMapTests 和回环测试盯，
/// 不再随 exe 发出去。
/// </summary>
[TestClass]
public sealed class ShippedSampleTests
{
    private sealed record Shipped(string FileName, string Text, MessageLibrary Library);

    /// <summary>
    /// 取随 exe 发的那一份样本。文件名同时是启动时的解包名和测试工程链接过来的名字，
    /// 改一处要改两处。
    /// </summary>
    private static Shipped Sample()
    {
        var text = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "TestData", AppDataPaths.SampleFileName));
        return new Shipped(AppDataPaths.SampleFileName, text, MessageLibrary.Load(text));
    }

    [TestMethod]
    public void TheSampleCarriesTheSectionSecsExpressRequires()
    {
        var sample = Sample();
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

    [TestMethod]
    public void TheSampleRoundTripsWithoutLosingAnything()
    {
        var sample = Sample();
        Assert.AreEqual(
            Flat(sample.Text),
            Flat(sample.Library.Export()),
            $"{sample.FileName} 读进再导出应该一个字不变——同事比对文件时不该看到一屏假改动。");
    }

    [TestMethod]
    public void EveryMessageInTheSampleIsSendable()
    {
        var sample = Sample();
        foreach (var entry in sample.Library.Messages)
        {
            Assert.IsTrue(
                SmlConverter.TryToSml(entry, out _, out var error),
                $"{sample.FileName} 的 {entry.Name}：{error}");
        }
    }

    [TestMethod]
    public void EveryAutoReplyInTheSamplePointsAtARealMessage()
    {
        var sample = Sample();
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

    /// <summary>比对排版前先统一两种写法：自闭合的空元素和行尾，理由同 MessageLibraryTests.Normalize。</summary>
    private static string Flat(string xml) =>
        xml.Replace(" />", "/>").Replace("\r\n", "\n").TrimEnd('\n');
}

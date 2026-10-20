using System.IO;
using System.Xml.Linq;
using QoderSecs.Core.Messages;
using QoderSecs.Core.Profiles;

namespace QoderSecs.Tests;

/// <summary>
/// 随 exe 解出去的每一份样本都要过的几条。盯的是「同事拿到 exe 第一眼导入的东西能不能用」：
/// SecsExpress 打不开样本，等于发布件是坏的——上一版就是这么翻车的，
/// 真因是缺 <c>&lt;DataDictionary&gt;</c> 那一段，单变量对照件的实测结论记在 MessageLibrary 里。
///
/// 「留结构不填数据」「消息清单」那些口径是 common-commands 自己的，
/// wafer-map 反过来靠填好的正文才有用，所以那些断言留在 SampleLibraryTests，不在这里查。
/// </summary>
[TestClass]
public sealed class ShippedSampleTests
{
    private sealed record Shipped(string FileName, string Text, MessageLibrary Library);

    /// <summary>
    /// 按 <see cref="AppDataPaths.SampleFileNames"/> 取每一份样本。这份清单同时是启动时
    /// 解包的清单和测试工程链接过来的文件名，加一份样本只改一处就会在这里撞到。
    /// </summary>
    private static IReadOnlyList<Shipped> AllSamples() =>
        AppDataPaths.SampleFileNames.Select(fileName =>
        {
            var text = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "TestData", fileName));
            return new Shipped(fileName, text, MessageLibrary.Load(text));
        }).ToList();

    [TestMethod]
    public void EverySampleCarriesTheSectionSecsExpressRequires()
    {
        foreach (var sample in AllSamples())
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
    public void EverySampleRoundTripsWithoutLosingAnything()
    {
        foreach (var sample in AllSamples())
        {
            Assert.AreEqual(
                Flat(sample.Text),
                Flat(sample.Library.Export()),
                $"{sample.FileName} 读进再导出应该一个字不变——同事比对文件时不该看到一屏假改动。");
        }
    }

    [TestMethod]
    public void EveryMessageInEverySampleIsSendable()
    {
        foreach (var sample in AllSamples())
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
    public void EveryAutoReplyInEverySamplePointsAtARealMessage()
    {
        foreach (var sample in AllSamples())
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

    /// <summary>
    /// README 对上这份样本的承诺是「导进去就能走完一轮 S12 上下图」，所以两件事必须是真的：
    /// S12F9 配好了指向 S12F16 的自动回复，而且那条回复里确实带着整张图。
    /// 图本身的内容和长度由 WaferMapTests 盯，这里只盯「这一轮跑得起来」。
    /// </summary>
    [TestMethod]
    public void TheWaferMapSampleRunsTheExchangeItAdvertises()
    {
        var shipped = AllSamples().FirstOrDefault(
            sample => sample.FileName == "wafer-map.sxml");
        Assert.IsNotNull(
            shipped, "样本清单里没有 wafer-map.sxml，或者测试工程没把它链接进 TestData/。");
        var sample = shipped!;

        var target = AutoReplyResolver.FindTarget(sample.Library, 12, 9);

        Assert.AreEqual("S12F16_WaferMap", target?.Name, "S12F9 的自动回复没配上，导入后不会回图。");
        Assert.IsTrue(
            SmlConverter.TryToMessage(target!, out var reply, out var error),
            $"{error}");
        using (reply)
        {
            Assert.IsFalse(reply.ReplyExpected, "回复的 S12F16 不能再带 W 位。");
        }
    }

    /// <summary>比对排版前先统一两种写法：自闭合的空元素和行尾，理由同 MessageLibraryTests.Normalize。</summary>
    private static string Flat(string xml) =>
        xml.Replace(" />", "/>").Replace("\r\n", "\n").TrimEnd('\n');
}

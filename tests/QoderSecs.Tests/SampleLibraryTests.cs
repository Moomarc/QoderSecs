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

    [TestMethod]
    public void ExportRoundTripsWithoutLosingDictionaryEntries()
    {
        var library = Sample();
        var reloaded = MessageLibrary.Load(library.Export());

        CollectionAssert.AreEqual(
            library.DataEntries.Select(entry => entry.Name).ToList(),
            reloaded.DataEntries.Select(entry => entry.Name).ToList());
    }

    /// <summary>
    /// 字典里放的是 CEID、SVID 这类要翻文档才能凑齐的变量，界面靠类型名渲染每一项，
    /// 认不出来的名字会显示成「未知元素类型」，所以整棵子树都得是已知类型。
    /// </summary>
    [TestMethod]
    public void DictionaryHoldsCeidAndSvidVariablesOfKnownTypes()
    {
        var entries = Sample().DataEntries;

        CollectionAssert.Contains(entries.Select(entry => entry.Name).ToList(), "CEID");
        CollectionAssert.Contains(entries.Select(entry => entry.Name).ToList(), "SVID");

        foreach (var element in entries.SelectMany(entry => new[] { entry.Element }.Concat(entry.Element.Descendants())))
        {
            if (element.Name == "Value" || element.Name == "Maps")
            {
                continue;
            }

            Assert.IsTrue(
                SecsItemTypes.TryParse(element.Name.LocalName, out _),
                $"字典条目里出现了认不出的元素 <{element.Name}>。");
        }
    }

    private static MessageLibrary Sample() => MessageLibrary.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "common-commands.sxml")));
}

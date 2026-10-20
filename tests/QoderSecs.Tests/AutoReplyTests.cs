using QoderSecs.Core.Messages;

namespace QoderSecs.Tests;

[TestClass]
public sealed class AutoReplyTests
{
    [TestMethod]
    public void FindTargetFollowsTheConfiguredName()
    {
        var target = AutoReplyResolver.FindTarget(Sample(), 1, 1);

        Assert.AreEqual("S1F2_OnlineData", target?.Name);
        Assert.AreEqual(1, target!.Stream);
        Assert.AreEqual(2, target.Function);
    }

    [TestMethod]
    public void FindTargetReturnsNullWhenNothingIsConfigured()
    {
        var library = Sample();

        // RecipeBody 在库里，但没配 AutoReply；S6F11 库里根本没有。默认就是不回。
        Assert.IsNull(AutoReplyResolver.FindTarget(library, 7, 25));
        Assert.IsNull(AutoReplyResolver.FindTarget(library, 6, 11));
    }

    [TestMethod]
    public void FindTargetReturnsNullWhenTheConfiguredNameIsGone()
    {
        var library = Sample();
        library.SetAutoReply(library.Messages[0], "已经删掉的消息");

        Assert.IsNull(AutoReplyResolver.FindTarget(library, 1, 1));
    }

    [TestMethod]
    public void FindTargetSkipsTheDuplicateWithoutConfiguration()
    {
        var library = Sample();
        library.SetAutoReply(library.Messages[0], null);
        var duplicate = library.NewMessage(1, 1, "S1F1_副本");
        library.SetAutoReply(duplicate, "S1F2_OnlineData");

        Assert.AreEqual("S1F2_OnlineData", AutoReplyResolver.FindTarget(library, 1, 1)?.Name);
    }

    /// <summary>回复发出去的是 Secondary，目标那条即使勾了 W 也不能带上线。</summary>
    [TestMethod]
    public void TheReplyIsBuiltWithoutTheWBit()
    {
        var target = AutoReplyResolver.FindTarget(Sample(), 1, 1)!;

        Assert.IsTrue(SmlConverter.TryToMessage(target, replyExpected: false, out var reply, out var error), error);

        using (reply)
        {
            Assert.IsFalse(reply!.ReplyExpected);
            Assert.AreEqual("S1F2_OnlineData", reply.Name);
            var sml = SmlConverter.ToSml(reply);
            StringAssert.Contains(sml, "'S1F2'");
            StringAssert.Contains(sml, "QODERSECS");
            Assert.IsFalse(sml.Contains("S1F2_OnlineData", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 引用是按名字存的，目标一改名就指不到了，所以直接置空，
    /// 不留断指——界面上的候选栏跟着就不会再列出旧名字，状态栏那句「已置空 N 条」也从这份名单来。
    /// </summary>
    [TestMethod]
    public void RenamingClearsTheReferrersThatPointedAtTheOldName()
    {
        var library = Sample();
        library.SetAutoReply(Entry(library, "RecipeBody"), "S1F2_OnlineData");
        library.SetAutoReply(library.NewMessage(9, 9, "回别的"), "S1F1_AreYouThere");

        library.SetName(Entry(library, "S1F2_OnlineData"), "S1F2_改名了");

        Assert.IsNull(Entry(library, "S1F1_AreYouThere").AutoReply, "指着旧名的引用要置空");
        Assert.IsNull(Entry(library, "RecipeBody").AutoReply, "指着旧名的有几条清几条");
        Assert.AreEqual("S1F1_AreYouThere", Entry(library, "回别的").AutoReply, "指着别人的不能跟着被清掉");
        Assert.HasCount(0, AutoReplyResolver.FindReferrers(library, "S1F2_OnlineData"));
        Assert.IsNull(AutoReplyResolver.FindTarget(library, 1, 1), "旧名已经没人叫了，回复不再命中");

        var exported = library.Export();
        Assert.IsFalse(exported.Contains("S1F2_OnlineData", StringComparison.Ordinal), "导出的文件里不该留下断指");
        Assert.IsFalse(exported.Contains("<AutoReply></AutoReply>", StringComparison.Ordinal), "置空是整个元素删掉，不留空壳");
    }

    /// <summary>名字没真变（只多敲了空格、或者改回原名）不算改名，指着它的引用不能跟着被清掉。</summary>
    [TestMethod]
    public void RenamingToTheSameNameKeepsTheReferrers()
    {
        var library = Sample();

        library.SetName(Entry(library, "S1F2_OnlineData"), "  S1F2_OnlineData  ");

        Assert.AreEqual("S1F2_OnlineData", Entry(library, "S1F1_AreYouThere").AutoReply);
        Assert.IsNotNull(AutoReplyResolver.FindTarget(library, 1, 1));
    }

    /// <summary>删除同理：目标没了，指着它的引用一并置空（删除确认框那句「会被置空」就是这么来的）。</summary>
    [TestMethod]
    public void DeletingClearsTheReferrersThatPointedAtIt()
    {
        var library = Sample();

        library.RemoveMessage(Entry(library, "S1F2_OnlineData"));

        Assert.IsNull(Entry(library, "S1F1_AreYouThere").AutoReply);
        Assert.IsNull(AutoReplyResolver.FindTarget(library, 1, 1));
        Assert.IsFalse(library.Export().Contains("<AutoReply></AutoReply>", StringComparison.Ordinal));
    }

    private static MessageEntry Entry(MessageLibrary library, string name) =>
        library.Messages.First(entry => entry.Name == name);

    [TestMethod]
    public void ReferrersMatchTheTargetNameTheSameWayFindTargetDoes()
    {
        var library = Sample();

        CollectionAssert.AreEqual(
            new[] { "S1F1_AreYouThere" },
            AutoReplyResolver.FindReferrers(library, " S1F2_OnlineData ").Select(entry => entry.Name).ToArray(),
            "名字两边的空格不算换了目标");
        Assert.HasCount(0, AutoReplyResolver.FindReferrers(library, null));
        Assert.HasCount(0, AutoReplyResolver.FindReferrers(library, "   "));
        Assert.HasCount(0, AutoReplyResolver.FindReferrers(library, "S1F1_AreYouThere"));
    }

    /// <summary>删除确认框那句「N 条自动回复指向它」不把用户正删的那条算进去。</summary>
    [TestMethod]
    public void ASelfPointingEntryCanBeLeftOutOfTheReferrers()
    {
        var library = Sample();
        var self = library.NewMessage(9, 9, "自己回自己");
        library.SetAutoReply(self, "自己回自己");

        Assert.HasCount(1, AutoReplyResolver.FindReferrers(library, "自己回自己"));
        Assert.HasCount(0, AutoReplyResolver.FindReferrers(library, "自己回自己", self));
    }

    private static MessageLibrary Sample() => MessageLibrary.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-library.sxml")));
}

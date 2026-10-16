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

    private static MessageLibrary Sample() => MessageLibrary.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-library.sxml")));
}

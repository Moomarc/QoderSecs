using QoderSecs.Core.Connection;

namespace QoderSecs.Tests;

[TestClass]
public sealed class Secs1AssemblerTests
{
    private static Secs1Block Block(
        int blockNumber,
        bool endOfMessage,
        int systemBytes = 0xAA,
        int bodyBytes = 2,
        int stream = 2,
        int function = 41)
    {
        // 正文按块号填满，拼完就能看出顺序有没有乱。
        var body = new byte[bodyBytes];
        body.AsSpan().Fill((byte)blockNumber);

        return new Secs1Block(
            1,
            true,
            new SecsHeader(stream, function, ReplyExpected: false),
            blockNumber,
            endOfMessage,
            systemBytes,
            body);
    }

    [TestMethod]
    public void SingleBlockWithEndBitIsOneMessage()
    {
        var assembler = new Secs1Assembler();
        var outcome = assembler.Feed(Block(1, endOfMessage: true));

        Assert.IsNull(outcome.Problem);
        Assert.IsNotNull(outcome.Message);
        Assert.HasCount(2, outcome.Message.Body);
        Assert.AreEqual(0xAA, outcome.Message.SystemBytes);
        Assert.AreEqual(0, assembler.PendingBlocks);
    }

    [TestMethod]
    public void BlocksAreJoinedInReceiptOrder()
    {
        var assembler = new Secs1Assembler();
        Assert.IsNull(assembler.Feed(Block(1, endOfMessage: false, bodyBytes: 3)).Message);
        Assert.IsNull(assembler.Feed(Block(2, endOfMessage: false, bodyBytes: 4)).Message);
        var outcome = assembler.Feed(Block(3, endOfMessage: true, bodyBytes: 5));

        Assert.IsNotNull(outcome.Message);
        byte[] expected = [1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 3];
        CollectionAssert.AreEqual(expected, outcome.Message.Body);
        Assert.AreEqual(0, assembler.PendingBlocks);
    }

    [TestMethod]
    public void MissingBlockInTheMiddleIsDroppedNotGuessed()
    {
        var assembler = new Secs1Assembler();
        assembler.Feed(Block(1, endOfMessage: false));
        var outcome = assembler.Feed(Block(3, endOfMessage: true));

        // 缺了第 2 块就拼不出正文，只能整条丢掉并说清原因；不能把第 3 块当成新消息发上去。
        Assert.IsNull(outcome.Message);
        StringAssert.Contains(outcome.Problem!, "块号不连续");
        Assert.AreEqual(0, assembler.PendingBlocks);
    }

    [TestMethod]
    public void NewSystemBytesStartsANewMessageEvenMidTransfer()
    {
        var assembler = new Secs1Assembler();
        assembler.Feed(Block(1, endOfMessage: false, systemBytes: 0x11));
        var outcome = assembler.Feed(Block(2, endOfMessage: true, systemBytes: 0x22));

        Assert.IsNull(outcome.Message);
        StringAssert.Contains(outcome.Problem!, "系统字节");
        Assert.IsNull(assembler.PendingSystemBytes);
    }

    [TestMethod]
    public void FirstBlockNumberedTwoIsNotAcceptedAsAMessageStart()
    {
        var assembler = new Secs1Assembler();
        var outcome = assembler.Feed(Block(2, endOfMessage: true));

        Assert.IsNull(outcome.Message);
        StringAssert.Contains(outcome.Problem!, "不是 1");
    }

    [TestMethod]
    public void AbandonReportsWhatWasThrownAway()
    {
        var assembler = new Secs1Assembler();
        assembler.Feed(Block(1, endOfMessage: false));
        Assert.AreEqual(1, assembler.PendingBlocks);
        Assert.AreEqual(0xAA, assembler.PendingSystemBytes);

        var problem = assembler.Abandon("等下一块超时");

        Assert.IsNotNull(problem);
        StringAssert.Contains(problem, "S2F41");
        StringAssert.Contains(problem, "等下一块超时");
        Assert.AreEqual(0, assembler.PendingBlocks);
        Assert.IsNull(assembler.Abandon("再叫一次"));
    }
}

using System.Threading.Channels;
using QoderSecs.Core.Connection;
using static QoderSecs.Tests.Secs1TestSupport;

namespace QoderSecs.Tests;

[TestClass]
public sealed class Secs1LineTests
{
    [TestMethod]
    public async Task EquipmentSendsAndHostReceivesThenReplies()
    {
        var (eqpStream, hostStream) = PipeStream.CreatePair();
        await using var eqp = new Secs1Line(eqpStream, Options(isEquipment: true));
        await using var host = new Secs1Line(hostStream, Options(isEquipment: false));
        eqp.Start();
        host.Start();

        var request = Message(6, 11, true, 0x0011_0001, 40);
        var sent = await eqp.SendAsync(request, Source());
        Assert.IsTrue(sent.Sent, sent.Detail);

        var received = await host.ReceiveMessageAsync(Source());
        Assert.AreEqual("S6F11W", received.Header.Signature);
        Assert.AreEqual(0x0011_0001, received.SystemBytes);
        CollectionAssert.AreEqual(request.Body, received.Body);

        // 回复照抄请求的系统字节，这是配对唯一认的东西。
        var reply = Message(6, 12, false, received.SystemBytes, 2);
        var replySent = await host.SendAsync(reply, Source());
        Assert.IsTrue(replySent.Sent, replySent.Detail);

        var gotReply = await eqp.ReceiveMessageAsync(Source());
        Assert.AreEqual("S6F12", gotReply.Header.Signature);
        Assert.AreEqual(0x0011_0001, gotReply.SystemBytes);
        Assert.AreEqual(0, host.ReceivedCount);
    }

    [TestMethod]
    public async Task LargeBodyIsSplitAndReassembledOverThreeBlocks()
    {
        var (eqpStream, hostStream) = PipeStream.CreatePair();
        await using var eqp = new Secs1Line(eqpStream, Options(isEquipment: true));
        await using var host = new Secs1Line(hostStream, Options(isEquipment: false));
        eqp.Start();
        host.Start();

        var request = Message(6, 13, true, 0x77, 600);
        var sent = await eqp.SendAsync(request, Source());
        Assert.IsTrue(sent.Sent, sent.Detail);

        var received = await host.ReceiveMessageAsync(Source());
        CollectionAssert.AreEqual(request.Body, received.Body);
        Assert.HasCount(600, received.Body);
    }

    /// <summary>
    /// NAK 之后要重新敲门再发同一块：对面回完 NAK 就退出接收状态了，
    /// 不敲门直接把块再甩一次它接不到。这条规矩是照 secs4j 的重试路径核出来的。
    /// </summary>
    [TestMethod]
    public async Task NakOpensTheDoorAgainAndResendsTheSameBlock()
    {
        var peer = new PipeStream();
        var trace = new List<string>();
        await using var line = new Secs1Line(peer, Options(isEquipment: true))
        {
            TraceSink = trace.Add
        };
        line.Start();

        var message = Message(2, 41, true, 0x55, 3);
        var block = message.ToBlocks(1, true)[0].Encode();
        var sending = line.SendAsync(message, Source());

        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Enq));
        peer.DeliverControl(Secs1Control.Eot);
        Assert.IsTrue(await peer.WrittenBytes(block));

        peer.DeliverControl(Secs1Control.Nak);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Enq, twice: true));
        peer.DeliverControl(Secs1Control.Eot);
        Assert.IsTrue(await peer.WrittenBytes(block, twice: true));

        peer.DeliverControl(Secs1Control.Ack);
        var outcome = await sending;
        Assert.IsTrue(outcome.Sent, outcome.Detail);
        StringAssert.Contains(Join(trace), "重新敲门再发这块");
    }

    [TestMethod]
    public async Task SendFailsWithAClearSentenceWhenNobodyAnswersTheDoor()
    {
        var peer = new PipeStream();
        await using var line = new Secs1Line(peer, new Secs1LineOptions(
            DeviceId: 1,
            IsEquipment: true,
            InterCharacterTimeout: TimeSpan.FromMilliseconds(300),
            ProtocolTimeout: TimeSpan.FromMilliseconds(200),
            InterBlockTimeout: TimeSpan.FromMilliseconds(200),
            RetryCount: 0));
        line.Start();

        var outcome = await line.SendAsync(Message(1, 1, true, 0x01, 0), Source());

        Assert.IsFalse(outcome.Sent);
        StringAssert.Contains(outcome.Detail, "没等到 EOT");
    }

    [TestMethod]
    public async Task CorruptedBlockIsAnsweredWithNakAndNeverBecomesAMessage()
    {
        var peer = new PipeStream();
        var trace = new List<string>();
        await using var line = new Secs1Line(peer, Options(isEquipment: false))
        {
            TraceSink = trace.Add
        };
        line.Start();

        var block = Message(1, 1, true, 0x09, 4).ToBlocks(1, true)[0].Encode();
        block[^1] ^= 0xFF;                       // 把校验和最后一个字节改掉

        peer.DeliverControl(Secs1Control.Enq);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Eot));  // 先回 EOT 表示准备接收
        peer.Deliver(block);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Nak));  // 再回 NAK

        Assert.AreEqual(0, line.ReceivedCount);
        Assert.IsTrue(trace.Any(text => text.Contains("校验和")), Join(trace));
    }

    [TestMethod]
    public async Task GoodBlockIsAcknowledgedAndTurnsIntoAMessage()
    {
        var peer = new PipeStream();
        await using var line = new Secs1Line(peer, Options(isEquipment: false));
        line.Start();

        var block = Message(2, 41, true, 0x31, 6).ToBlocks(1, true)[0].Encode();
        peer.DeliverControl(Secs1Control.Enq);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Eot));
        peer.Deliver(block);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Ack));

        var received = await line.ReceiveMessageAsync(Source());
        Assert.AreEqual("S2F41W", received.Header.Signature);
        Assert.AreEqual(0x31, received.SystemBytes);
        Assert.HasCount(6, received.Body);
    }

    [TestMethod]
    public async Task MultiBlockReceiveWaitsForTheNextDoorKnockBeforeTheEndBit()
    {
        var peer = new PipeStream();
        await using var line = new Secs1Line(peer, Options(isEquipment: false));
        line.Start();

        var blocks = Message(6, 11, true, 0x42, 300).ToBlocks(1, true);
        Assert.HasCount(2, blocks);

        peer.DeliverControl(Secs1Control.Enq);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Eot));
        peer.Deliver(blocks[0].Encode());
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Ack));
        Assert.AreEqual(0, line.ReceivedCount);

        // 第二块要重新敲门；这条规矩两个参考实现（secs4java、secs4js）都是这么做的。
        peer.DeliverControl(Secs1Control.Enq);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Eot, twice: true));
        peer.Deliver(blocks[1].Encode());
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Ack, twice: true));

        var received = await line.ReceiveMessageAsync(Source());
        Assert.HasCount(300, received.Body);
    }

    [TestMethod]
    public async Task HalfDuplexCollisionReceivesThePeerFirstThenResendsOurOwn()
    {
        var peer = new PipeStream();
        var trace = new List<string>();
        await using var line = new Secs1Line(peer, Options(isEquipment: true))
        {
            TraceSink = trace.Add
        };
        line.Start();

        var ours = Message(1, 1, true, 0x99, 0);
        var theirs = Message(6, 11, true, 0x88, 5).ToBlocks(1, false)[0].Encode();
        var sending = line.SendAsync(ours, Source());

        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Enq));
        // 我们敲门，撞见对方也在敲门：先当接收方，把它这条收完，再重发自己那条。
        peer.DeliverControl(Secs1Control.Enq);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Eot));
        peer.Deliver(theirs);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Ack));

        var received = await line.ReceiveMessageAsync(Source());
        Assert.AreEqual("S6F11W", received.Header.Signature);

        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Enq, twice: true));
        peer.DeliverControl(Secs1Control.Eot);
        Assert.IsTrue(await peer.WrittenBytes(ours.ToBlocks(1, true)[0].Encode()));
        peer.DeliverControl(Secs1Control.Ack);

        var outcome = await sending;
        Assert.IsTrue(outcome.Sent, outcome.Detail);
        Assert.IsTrue(trace.Any(text => text.Contains("撞车")), Join(trace));
    }

    [TestMethod]
    public async Task LineEndsAndReportsWhenThePeerStopsTalking()
    {
        var peer = new PipeStream();
        var trace = new List<string>();
        await using var line = new Secs1Line(peer, Options(isEquipment: false))
        {
            TraceSink = trace.Add
        };
        line.Start();

        peer.DeliverEnd();
        await WaitForTraceAsync(trace, "线上没有更多字节");
        StringAssert.Contains(Join(trace), "对端断开或端口已关闭");
        await Assert.ThrowsExactlyAsync<ChannelClosedException>(
            () => line.ReceiveMessageAsync(CancellationToken.None));
    }

    [TestMethod]
    public void TraceSentencesNameEveryControlByte()
    {
        Assert.AreEqual("ENQ", Secs1Line.Name(Secs1Control.Enq));
        Assert.AreEqual("EOT", Secs1Line.Name(Secs1Control.Eot));
        Assert.AreEqual("ACK", Secs1Line.Name(Secs1Control.Ack));
        Assert.AreEqual("NAK", Secs1Line.Name(Secs1Control.Nak));
        Assert.AreEqual("SRQ", Secs1Line.Name(Secs1Control.Srq));
        Assert.AreEqual("EIC", Secs1Line.Name(Secs1Control.Eic));
    }

    /// <summary>
    /// 一个字段不合法的块（校验和是对的）不能把接收线程打死：回 NAK，然后照常收下一条。
    /// 这是真机台会做的事，S0 或 F0 只要发一次，之前的写法整条线就永久废了。
    /// </summary>
    [TestMethod]
    public async Task IllegalHeaderFieldsGetNakAndTheLineKeepsReceiving()
    {
        var peer = new PipeStream();
        var trace = new List<string>();
        await using var line = new Secs1Line(peer, Options(isEquipment: false))
        {
            TraceSink = trace.Add
        };
        line.Start();

        peer.DeliverControl(Secs1Control.Enq);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Eot));
        peer.Deliver(RawFrame(stream: 0, function: 15));
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Nak));
        Assert.AreEqual(0, line.ReceivedCount);
        StringAssert.Contains(Join(trace), "S0");

        var block = Message(6, 11, true, 0x31, 4).ToBlocks(1, true)[0];
        peer.DeliverControl(Secs1Control.Enq);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Eot, twice: true));
        peer.Deliver(block.Encode());
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Ack));

        var received = await line.ReceiveMessageAsync(Source());
        Assert.AreEqual("S6F11W", received.Header.Signature);
    }

    /// <summary>
    /// 敲完门到对方真把块发出来，中间停一下是正常的（机台在准备正文）。
    /// 这一段等的是 T2，不是块内字符间隔那一档：按 secs4j 的读法，长度字节用 t2，块内才用 t1。
    /// </summary>
    [TestMethod]
    public async Task LengthByteIsWaitedForWithT2NotTheInterCharacterGap()
    {
        var peer = new PipeStream();
        await using var line = new Secs1Line(peer, new Secs1LineOptions(
            DeviceId: 1,
            IsEquipment: false,
            InterCharacterTimeout: TimeSpan.FromMilliseconds(200),
            ProtocolTimeout: TimeSpan.FromSeconds(2),
            InterBlockTimeout: TimeSpan.FromSeconds(2),
            RetryCount: 0));
        line.Start();

        var block = Message(6, 11, true, 0x42, 4).ToBlocks(1, true)[0];
        peer.DeliverControl(Secs1Control.Enq);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Eot));

        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            peer.Deliver(block.Encode());
        });

        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Ack));
        var received = await line.ReceiveMessageAsync(Source());
        Assert.AreEqual("S6F11W", received.Header.Signature);
    }

    /// <summary>对方敲了门却一直没把块发出来：等到 T2 要回一个 NAK，不能让人家干等。</summary>
    [TestMethod]
    public async Task ReceiverNaksWhenNoBlockEverComesAfterTheDoor()
    {
        var peer = new PipeStream();
        var trace = new List<string>();
        await using var line = new Secs1Line(peer, new Secs1LineOptions(
            DeviceId: 1,
            IsEquipment: false,
            InterCharacterTimeout: TimeSpan.FromMilliseconds(200),
            ProtocolTimeout: TimeSpan.FromMilliseconds(300),
            InterBlockTimeout: TimeSpan.FromMilliseconds(300),
            RetryCount: 0))
        {
            TraceSink = trace.Add
        };
        line.Start();

        peer.DeliverControl(Secs1Control.Enq);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Eot));
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Nak));
        StringAssert.Contains(Join(trace), "没等到块的长度字节");
    }

    /// <summary>本端取消（关端口、退出程序）要说成断开，不能报成「对方不回」。</summary>
    [TestMethod]
    public async Task ClosingTheLineSaysItWasClosedNotThatThePeerWasSilent()
    {
        var peer = new PipeStream();
        await using var line = new Secs1Line(peer, Options(isEquipment: true));
        line.Start();

        using var cts = new CancellationTokenSource();
        var sending = line.SendAsync(Message(1, 1, true, 0x02, 0), cts.Token);
        Assert.IsTrue(await peer.WrittenByte(Secs1Control.Enq));

        cts.Cancel();
        var outcome = await sending;
        Assert.IsFalse(outcome.Sent);
        StringAssert.Contains(outcome.Detail, "被关掉");
    }

    /// <summary>切不出块的消息（流号超 127、正文大到块号装不下）交出去的是一句话，不是异常。</summary>
    [TestMethod]
    public async Task MessagesThatCannotBeSplitIntoBlocksComeBackAsSentences()
    {
        var peer = new PipeStream();
        await using var line = new Secs1Line(peer, Options(isEquipment: true));
        line.Start();

        var overStream = await line.SendAsync(Message(200, 11, true, 0x03, 0), Source());
        Assert.IsFalse(overStream.Sent);
        StringAssert.Contains(overStream.Detail, "S200 发不出去");

        // 块号只有 15 位：每块 244 字节，超过 32767 块就拼不出一条消息。
        var tooBig = await line.SendAsync(Message(6, 11, true, 0x04, 244 * 32_768), Source());
        Assert.IsFalse(tooBig.Sent);
        StringAssert.Contains(tooBig.Detail, "装不下");
    }

    private static string Join(List<string> trace) => string.Join(" / ", trace);
}

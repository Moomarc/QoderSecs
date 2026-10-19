using System.Buffers;
using QoderSecs.Core.Connection;
using QoderSecs.Core.Messages;
using Secs4Net;
using static QoderSecs.Tests.Secs1TestSupport;

namespace QoderSecs.Tests;

/// <summary>
/// 这一层不自己写 SECS-II 编码，用的是 Secs4Net 那份——和 HSMS 落盘的同一个函数。
/// 所以这里只验换算本身：字节没被加头去尾、块切完还能解回来、坏正文说的是人话。
/// </summary>
[TestClass]
public sealed class Secs1PayloadTests
{
    [TestMethod]
    public void FrameBodyIsTheSameBytesHsmsPutsOnTheWire()
    {
        var item = Item.L(Item.A("QODERSECS"), Item.U2(new ushort[] { 1, 2 }));
        using var message = new SecsMessage(2, 41, replyExpected: true)
        {
            Name = "PP_SELECT",
            SecsItem = item
        };

        var frame = Secs1Payload.ToFrame(message, 0x11223344);

        var writer = new ArrayBufferWriter<byte>();
        item.EncodeTo(writer);
        CollectionAssert.AreEqual(writer.WrittenSpan.ToArray(), frame.Body);

        Assert.AreEqual("S2F41W", frame.Header.Signature);
        Assert.AreEqual("PP_SELECT", frame.Header.Name);
        Assert.AreEqual(0x11223344, frame.SystemBytes);
    }

    [TestMethod]
    public void RoundTripThroughBlocksKeepsEveryValue()
    {
        using var sent = new SecsMessage(6, 11, replyExpected: true)
        {
            Name = "S6F11",
            SecsItem = Item.L(Item.A("RECIPE"), Item.F8(new double[] { 3.5, -2.25 }), Item.B(new byte[700]))
        };
        var frame = Secs1Payload.ToFrame(sent, 0x77);

        var assembler = new Secs1Assembler();
        Secs1Message? joined = null;
        foreach (var block in frame.ToBlocks(1, fromEquipment: true))
        {
            joined = assembler.Feed(block).Message;
        }

        Assert.IsNotNull(joined);
        Assert.IsTrue(Secs1Payload.TryToMessage(joined, out var received, out var error), error);
        Assert.IsNotNull(received);
        try
        {
            Assert.AreEqual(6, received.S);
            Assert.AreEqual(11, received.F);
            Assert.IsTrue(received.ReplyExpected);
            Assert.AreEqual(SmlConverter.ToSml(sent), SmlConverter.ToSml(received));
        }
        finally
        {
            received.Dispose();
        }
    }

    [TestMethod]
    public void MessageWithoutBodyIsNotReportedAsBroken()
    {
        var frame = new Secs1Message(new SecsHeader(1, 1, ReplyExpected: true), 0x01, []);

        Assert.IsTrue(Secs1Payload.TryToMessage(frame, out var message, out var error));
        Assert.IsNull(error);
        Assert.IsNotNull(message);
        Assert.AreEqual(1, message.S);
        Assert.AreEqual(1, message.F);
        Assert.IsTrue(message.ReplyExpected);
        message.Dispose();
    }

    [TestMethod]
    public void TruncatedBodyIsReportedAsASentenceNotAnException()
    {
        var whole = Secs1Payload.Encode(Item.A("HELLO WORLD"));
        var frame = new Secs1Message(new SecsHeader(2, 41, ReplyExpected: false), 0x01, whole[..^3]);

        Assert.IsFalse(Secs1Payload.TryToMessage(frame, out var message, out var error));
        Assert.IsNull(message);
        StringAssert.Contains(error!, "SECS-II");
    }

    [TestMethod]
    public void ExtraBytesAfterTheItemAreNotReadAsPartOfIt()
    {
        byte[] body = [.. Secs1Payload.Encode(Item.A("HELLO")), 0x00, 0x00];
        var frame = new Secs1Message(new SecsHeader(2, 41, ReplyExpected: false), 0x01, body);

        Assert.IsFalse(Secs1Payload.TryToMessage(frame, out var message, out var error));
        Assert.IsNull(message);
        StringAssert.Contains(error!, "多出");
    }

    /// <summary>带着真实的值走一遍整条线：切块、握手、拼回来、解码，最后比对 SML。</summary>
    [TestMethod]
    public async Task MessageSentOverTheLineArrivesWithItsValuesIntact()
    {
        var (eqpStream, hostStream) = PipeStream.CreatePair();
        await using var eqp = new Secs1Line(eqpStream, Options(isEquipment: true));
        await using var host = new Secs1Line(hostStream, Options(isEquipment: false));
        eqp.Start();
        host.Start();

        using var request = new SecsMessage(2, 41, replyExpected: true)
        {
            Name = "PP_SELECT",
            SecsItem = Item.L(Item.A("RECIPE"), Item.B(new byte[300]))
        };
        var sent = await eqp.SendAsync(Secs1Payload.ToFrame(request, 0x55AA), Source());
        Assert.IsTrue(sent.Sent, sent.Detail);

        var frame = await host.ReceiveMessageAsync(Source());
        Assert.IsTrue(Secs1Payload.TryToMessage(frame, out var received, out var error), error);
        Assert.IsNotNull(received);
        try
        {
            Assert.AreEqual(SmlConverter.ToSml(request), SmlConverter.ToSml(received));
        }
        finally
        {
            received.Dispose();
        }
    }
}

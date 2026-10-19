using QoderSecs.Core.Connection;
using static QoderSecs.Tests.Secs1TestSupport;

namespace QoderSecs.Tests;

[TestClass]
public sealed class Secs1BlockTests
{
    private static Secs1Block Block(
        byte[]? body = null,
        int deviceId = 1,
        bool fromEquipment = false,
        int stream = 6,
        int function = 11,
        bool replyExpected = true,
        int blockNumber = 1,
        bool endOfMessage = true,
        int systemBytes = 0x12345678) =>
        new(deviceId, fromEquipment, new SecsHeader(stream, function, replyExpected),
            blockNumber, endOfMessage, systemBytes, body ?? []);

    [TestMethod]
    public void EncodedBlockMatchesTheStandardByteLayout()
    {
        // 没有正文的一条 S6F11W：长度 10，头 10 字节，校验和两字节，共 13。
        var frame = Block().Encode();

        Assert.HasCount(13, frame);
        Assert.AreEqual(0x0A, frame[0]);
        Assert.AreEqual(0x00, frame[1]);          // R=0（Host 发出）+ 设备号高字节
        Assert.AreEqual(0x01, frame[2]);          // 设备号低字节
        Assert.AreEqual(0x86, frame[3]);          // W=1 + 流号 6
        Assert.AreEqual(0x0B, frame[4]);          // 功能号 11
        Assert.AreEqual(0x80, frame[5]);          // E=1（末块）+ 块号高字节
        Assert.AreEqual(0x01, frame[6]);          // 块号低字节
        Assert.AreEqual(0x12, frame[7]);
        Assert.AreEqual(0x34, frame[8]);
        Assert.AreEqual(0x56, frame[9]);
        Assert.AreEqual(0x78, frame[10]);

        // 校验和是头里 10 个字节逐字节相加，大端两字节。
        var sum = 0x00 + 0x01 + 0x86 + 0x0B + 0x80 + 0x01 + 0x12 + 0x34 + 0x56 + 0x78;
        Assert.AreEqual((byte)(sum >> 8), frame[11]);
        Assert.AreEqual((byte)sum, frame[12]);
    }

    [TestMethod]
    public void EquipmentDirectionSetsTheRBit()
    {
        var frame = Block(fromEquipment: true, deviceId: 300).Encode();

        Assert.AreEqual(0x81, frame[1]);          // R=1，设备号高字节仍是 0x01（300 >> 8）
        Assert.AreEqual(0x2C, frame[2]);          // 300 的低字节

        Assert.IsTrue(Secs1Block.TryDecode(frame, out var back, out _));
        Assert.IsNotNull(back);
        Assert.IsTrue(back.FromEquipment);
        Assert.AreEqual(300, back.DeviceId);
    }

    [TestMethod]
    public void EncodeThenDecodeReturnsEveryField()
    {
        var body = new byte[20];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(i * 3);
        }

        var block = Block(body: body, blockNumber: 7, endOfMessage: false, replyExpected: false);
        Assert.IsTrue(Secs1Block.TryDecode(block.Encode(), out var back, out var error));
        Assert.IsNull(error);
        Assert.IsNotNull(back);

        Assert.AreEqual(1, back.DeviceId);
        Assert.IsFalse(back.FromEquipment);
        Assert.AreEqual(6, back.Header.Stream);
        Assert.AreEqual(11, back.Header.Function);
        Assert.IsFalse(back.Header.ReplyExpected);
        Assert.AreEqual(7, back.BlockNumber);
        Assert.IsFalse(back.EndOfMessage);
        Assert.AreEqual(0x12345678, back.SystemBytes);
        CollectionAssert.AreEqual(body, back.Body);
        Assert.AreEqual(30, back.Length);
    }

    [TestMethod]
    public void SingleBlockMessageMayNumberItsBlockZero()
    {
        var block = Block(blockNumber: 0);

        Assert.IsTrue(block.IsFirstBlock);
        Assert.IsTrue(Secs1Block.TryDecode(block.Encode(), out var back, out _));
        Assert.IsNotNull(back);
        Assert.AreEqual(0, back.BlockNumber);
        Assert.IsTrue(back.IsFirstBlock);
    }

    [TestMethod]
    public void OneBadByteMakesTheChecksumFail()
    {
        var frame = Block([0xAA, 0xBB]).Encode();
        frame[^2] ^= 0x01;

        Assert.IsFalse(Secs1Block.TryDecode(frame, out var block, out var error));
        Assert.IsNull(block);
        StringAssert.Contains(error!, "校验和");
    }

    [TestMethod]
    public void LengthByteOutsideTenToTwoHundredFiftyFourIsRejected()
    {
        // 13 字节够长，所以先过「整块太短」这一关，报的必须是长度字节不对。
        Assert.IsFalse(Secs1Block.TryDecode([0x09, .. new byte[12]], out _, out var error));
        StringAssert.Contains(error!, "长度字节");

        var tooLong = new byte[258];
        tooLong[0] = 0xFF;
        Assert.IsFalse(Secs1Block.TryDecode(tooLong, out _, out error));
        StringAssert.Contains(error!, "长度字节");
    }

    [TestMethod]
    public void FrameShorterThanTheDeclaredLengthIsRejected()
    {
        var frame = Block([0x01, 0x02]).Encode();

        Assert.IsFalse(Secs1Block.TryDecode(frame[..^1], out _, out var error));
        StringAssert.Contains(error!, "实际只有");
    }

    /// <summary>
    /// 校验和对得上、但头里的流号或功能号不合法：这叫「TryDecode」，就不能往外抛异常。
    /// 抛出去的话，接收线程只有一次，一个这样的块能把整条线打死。
    /// </summary>
    [TestMethod]
    public void IllegalHeaderFieldsAreRejectedAsSentencesNotAsExceptions()
    {
        Assert.IsFalse(Secs1Block.TryDecode(RawFrame(stream: 0, function: 15), out var block, out var error));
        Assert.IsNull(block);
        StringAssert.Contains(error!, "S0");

        Assert.IsFalse(Secs1Block.TryDecode(RawFrame(stream: 6, function: 0), out block, out error));
        Assert.IsNull(block);
        StringAssert.Contains(error!, "F0");
    }

    [TestMethod]
    public void LegalHeaderFieldsDecodeIntoABlock()
    {
        Assert.IsTrue(Secs1Block.TryDecode(RawFrame(stream: 6, function: 11, replyExpected: true), out var block, out var error));
        Assert.IsNull(error);
        Assert.IsNotNull(block);
        Assert.AreEqual("S6F11W", block.Header.Signature);
        Assert.IsTrue(block.EndOfMessage);
        Assert.AreEqual(1, block.BlockNumber);
    }

    [TestMethod]
    public void BodyBeyondTwoHundredFortyFourBytesDoesNotFitOneBlock()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Block(body: new byte[Secs1Block.MaxBodyBytes + 1]));

        // 刚好 244 要能通过，上限不能卡在一格之外。
        Assert.HasCount(Secs1Block.MaxBodyBytes, Block(body: new byte[Secs1Block.MaxBodyBytes]).Body);
    }

    [TestMethod]
    public void StreamAboveOneHundredTwentySevenDoesNotFitTheHeader()
    {
        var error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Block(stream: 128, function: 1)).Message;

        // HSMS 的 S 可以到 255，SECS-I 的头只留了 7 位；这句话要在状态栏里看得懂。
        StringAssert.Contains(error!, "HSMS");
    }

    [TestMethod]
    public void DeviceIdAboveFifteenBitsIsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Block(deviceId: 32768));
    }

    [TestMethod]
    public void BlocksAreSplitByTwentyFortyFourBytesAndNumberedUpToTheEndBit()
    {
        var message = Secs1TestSupport.Message(6, 11, true, 0x11223344, 600);
        var blocks = message.ToBlocks(1, false);

        Assert.HasCount(3, blocks);
        Assert.HasCount(244, blocks[0].Body);
        Assert.HasCount(244, blocks[1].Body);
        Assert.HasCount(112, blocks[2].Body);
        Assert.AreEqual(1, blocks[0].BlockNumber);
        Assert.AreEqual(3, blocks[2].BlockNumber);
        Assert.IsFalse(blocks[0].EndOfMessage);
        Assert.IsTrue(blocks[^1].EndOfMessage);
        Assert.IsTrue(blocks.All(block => block.SystemBytes == 0x11223344));
        Assert.AreEqual(13, Secs1Block.FrameBytes(0));
    }

    [TestMethod]
    public void EmptyBodyStillMakesOneBlockBecauseTheLineNeedsAHeaderAndChecksum()
    {
        var blocks = Secs1TestSupport.Message(1, 1, true, 0x01, 0).ToBlocks(1, false);

        Assert.HasCount(1, blocks);
        Assert.AreEqual(10, blocks[0].Length);
    }
}

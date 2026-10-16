using QoderSecs.Core.Connection;

namespace QoderSecs.Tests;

[TestClass]
public sealed class TranscriptCorrelatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void OutboundRequestWithWBitAwaitsSecondary()
    {
        var correlator = new TranscriptCorrelator();
        var record = correlator.RecordOutbound(Header(1, 1, true), 0x11223344, "S1F1", T0);

        Assert.AreEqual(SecsDirection.Out, record.Direction);
        Assert.AreEqual(RecordStatus.AwaitingSecondary, record.Status);
        Assert.AreEqual("等待回复", record.StatusText);
        Assert.IsNull(record.ElapsedMilliseconds);
    }

    [TestMethod]
    public void OutboundMessageWithoutWBitIsJustSent()
    {
        var correlator = new TranscriptCorrelator();
        var record = correlator.RecordOutbound(Header(2, 41, false), 0x1, "S2F41", T0);

        Assert.AreEqual(RecordStatus.Sent, record.Status);
        Assert.AreEqual("已发送", record.StatusText);
    }

    [TestMethod]
    public void SecondaryMatchesRequestBySystemBytesAndRecordsElapsed()
    {
        var correlator = new TranscriptCorrelator();
        var request = correlator.RecordOutbound(Header(1, 1, true), 0xAA, "S1F1", T0);
        var reply = correlator.RecordInbound(Header(1, 2, false, "S1F2"), 0xAA, "S1F2", T0.AddMilliseconds(123));

        Assert.AreEqual(RecordStatus.SecondaryReceived, request.Status);
        Assert.AreEqual(123, request.ElapsedMilliseconds);
        Assert.AreEqual(RecordStatus.PairedSecondary, reply.Status);
        Assert.AreEqual(0xAA, reply.LinkedSystemBytes);
        Assert.HasCount(2, correlator.Records);
    }

    [TestMethod]
    public void SecondaryWithOtherSystemBytesNeverMatchesTheWaitingRequest()
    {
        var correlator = new TranscriptCorrelator();
        var request = correlator.RecordOutbound(Header(1, 1, true), 0xAA, "S1F1", T0);
        var orphan = correlator.RecordInbound(Header(1, 2, false), 0xBB, "S1F2", T0.AddSeconds(1));

        Assert.AreEqual(RecordStatus.AwaitingSecondary, request.Status);
        Assert.AreEqual(RecordStatus.OrphanSecondary, orphan.Status);
        Assert.AreEqual("无人等待的回复", orphan.StatusText);
    }

    [TestMethod]
    public void TimeoutKeepsTheRequestAndLateSecondaryIsMarked()
    {
        var correlator = new TranscriptCorrelator();
        var request = correlator.RecordOutbound(Header(1, 1, true), 0xCC, "S1F1", T0);
        correlator.MarkTimedOut(0xCC, T0.AddSeconds(45));

        Assert.AreEqual(RecordStatus.TimedOut, request.Status);
        Assert.AreEqual("T3 超时", request.StatusText);
        Assert.AreEqual(45000, request.ElapsedMilliseconds);

        var late = correlator.RecordInbound(Header(1, 2, false), 0xCC, "S1F2", T0.AddSeconds(50));

        Assert.AreEqual(RecordStatus.LateSecondary, late.Status);
        Assert.AreEqual("迟到回复", late.StatusText);
        Assert.AreEqual(RecordStatus.TimedOut, request.Status);
    }

    [TestMethod]
    public void MarkTimedOutIgnoresUnknownSystemBytes()
    {
        var correlator = new TranscriptCorrelator();
        correlator.RecordOutbound(Header(1, 1, true), 0xAA, "S1F1", T0);
        correlator.MarkTimedOut(0xBB, T0.AddSeconds(1));

        Assert.AreEqual(RecordStatus.AwaitingSecondary, correlator.Records[0].Status);
    }

    [TestMethod]
    public void InboundRequestWithWBitAwaitsLocalReply()
    {
        var correlator = new TranscriptCorrelator();
        var request = correlator.RecordInbound(Header(1, 1, true), 0x01, "S1F1", T0);

        Assert.AreEqual(SecsDirection.In, request.Direction);
        Assert.AreEqual(RecordStatus.AwaitingReply, request.Status);
        Assert.AreEqual("待本端回复", request.StatusText);
    }

    [TestMethod]
    public void InboundMessageWithoutWBitIsReceived()
    {
        var correlator = new TranscriptCorrelator();
        var record = correlator.RecordInbound(Header(6, 11, false), 0x02, "S6F11", T0);

        Assert.AreEqual(RecordStatus.Received, record.Status);
        Assert.AreEqual("已收到", record.StatusText);
    }

    [TestMethod]
    public void LocalReplyMarksTheInboundRequestAsAnswered()
    {
        var correlator = new TranscriptCorrelator();
        var request = correlator.RecordInbound(Header(1, 1, true), 0x03, "S1F1", T0);
        var reply = correlator.RecordOutbound(Header(1, 2, false, "S1F2"), 0x03, "S1F2", T0.AddMilliseconds(5));

        Assert.AreEqual(RecordStatus.Answered, request.Status);
        Assert.AreEqual("已自动回复", request.StatusText);
        Assert.AreEqual(RecordStatus.Sent, reply.Status);
    }

    [TestMethod]
    public void SkippingTheAutoReplyReplacesTheWaitingStatus()
    {
        var correlator = new TranscriptCorrelator();
        var request = correlator.RecordInbound(Header(1, 1, true), 0x21, "S1F1", T0);

        correlator.MarkAutoReplySkipped(0x21);

        Assert.AreEqual(RecordStatus.AutoReplySkipped, request.Status);
        Assert.AreEqual("未自动回复", request.StatusText);
    }

    [TestMethod]
    public void SkippingDoesNotUndoAReplyThatAlreadyWentOut()
    {
        var correlator = new TranscriptCorrelator();
        var request = correlator.RecordInbound(Header(1, 1, true), 0x22, "S1F1", T0);
        correlator.RecordOutbound(Header(1, 2, false), 0x22, "S1F2", T0.AddMilliseconds(3));

        correlator.MarkAutoReplySkipped(0x22);

        Assert.AreEqual(RecordStatus.Answered, request.Status);
    }

    [TestMethod]
    public void SkippingAMessageThatNeverExpectedAReplyChangesNothing()
    {
        var correlator = new TranscriptCorrelator();
        var received = correlator.RecordInbound(Header(2, 41, false), 0x23, "S2F41", T0);

        correlator.MarkAutoReplySkipped(0x23);

        Assert.AreEqual(RecordStatus.Received, received.Status);
    }

    [TestMethod]
    public void RecordAddedFiresForEveryMessage()
    {
        var correlator = new TranscriptCorrelator();
        var added = new List<CommunicationRecord>();
        correlator.RecordAdded += record => added.Add(record);

        correlator.RecordOutbound(Header(1, 1, true), 0x04, "S1F1", T0);
        correlator.RecordInbound(Header(1, 2, false), 0x04, "S1F2", T0);

        Assert.HasCount(2, added);
        Assert.AreEqual(0x04, added[1].SystemBytes);
        Assert.AreEqual("00000004", added[1].SystemBytesText);
    }

    [TestMethod]
    public void StatusChangesRaisePropertyChangedForTheBoundRow()
    {
        var correlator = new TranscriptCorrelator();
        var request = correlator.RecordOutbound(Header(1, 1, true), 0x05, "S1F1", T0);
        var changes = new List<string?>();
        request.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        correlator.RecordInbound(Header(1, 2, false), 0x05, "S1F2", T0.AddMilliseconds(10));

        CollectionAssert.Contains(changes, nameof(CommunicationRecord.Status));
        CollectionAssert.Contains(changes, nameof(CommunicationRecord.StatusText));
        CollectionAssert.Contains(changes, nameof(CommunicationRecord.ElapsedText));
    }

    [TestMethod]
    public void ClearPendingForgetsWaitingRequestsButKeepsTheHistory()
    {
        var correlator = new TranscriptCorrelator();
        correlator.RecordOutbound(Header(1, 1, true), 0x06, "S1F1", T0);
        correlator.RecordInbound(Header(1, 1, true), 0x07, "S1F1", T0);
        correlator.ClearPending();

        Assert.HasCount(2, correlator.Records);
        var orphan = correlator.RecordInbound(Header(1, 2, false), 0x06, "S1F2", T0.AddSeconds(1));

        Assert.AreEqual(RecordStatus.OrphanSecondary, orphan.Status);
    }

    [TestMethod]
    public void ClearRemovesEverything()
    {
        var correlator = new TranscriptCorrelator();
        correlator.RecordOutbound(Header(1, 1, true), 0x08, "S1F1", T0);
        correlator.Clear();

        Assert.IsEmpty(correlator.Records);
    }

    [TestMethod]
    public void DetailTextIsTimePlusTheSmlWithoutRepeatingTheListColumns()
    {
        var correlator = new TranscriptCorrelator();
        var request = correlator.RecordOutbound(Header(1, 1, true, "远程命令"), 0x09, "<S1F1W />", T0);
        correlator.RecordInbound(Header(1, 2, false), 0x09, "S1F2", T0.AddMilliseconds(45));

        var detail = request.DetailText;

        StringAssert.StartsWith(detail, $"时间     {request.TimeText}");
        StringAssert.EndsWith(detail, "<S1F1W />");
        // 方向、消息、SystemBytes、状态列表那一行都有，详情里不重复。
        StringAssert.DoesNotMatch(detail, new System.Text.RegularExpressions.Regex("方向|系统字节|状态"));
    }

    [TestMethod]
    public void DetailTextFoldsTheBodyWhenItOnlyLivesInTheLog()
    {
        var correlator = new TranscriptCorrelator();
        var record = correlator.RecordInbound(Header(6, 11, false), 0x0B, null, T0);
        record.SmlSequence = 7;
        record.SmlByteLength = 20 * 1024 * 1024;

        var detail = record.DetailText;

        StringAssert.Contains(detail, "sml.log");
        StringAssert.Contains(detail, "20.00 MB");
    }

    [TestMethod]
    public void MessageTextFallsBackToTheSignatureWithoutAName()
    {
        var correlator = new TranscriptCorrelator();
        var record = correlator.RecordInbound(Header(2, 41, false), 0x0A, "S2F41", T0);

        Assert.AreEqual("S2F41", record.MessageText);
        Assert.AreEqual("收到", record.DirectionText);
    }

    /// <summary>断线时还挂着的记录要落到终态，不然界面上永远显示「等待回复」。</summary>
    [TestMethod]
    public void ClearPendingMarksTheStillWaitingRecordsAsLinkClosed()
    {
        var correlator = new TranscriptCorrelator();
        var outbound = correlator.RecordOutbound(Header(1, 1, true), 0x01, "<S1F1W />", T0);
        var inbound = correlator.RecordInbound(Header(2, 41, true), 0x02, "<S2F41W />", T0);
        Assert.AreEqual(RecordStatus.AwaitingSecondary, outbound.Status);
        Assert.AreEqual(RecordStatus.AwaitingReply, inbound.Status);

        correlator.ClearPending();

        Assert.AreEqual(RecordStatus.LinkClosed, outbound.Status);
        Assert.AreEqual("连接已断开", outbound.StatusText);
        Assert.AreEqual(RecordStatus.LinkClosed, inbound.Status);
    }

    /// <summary>同一条请求只认一次迟到回复，第二条要报成无人等待，配对表也不能一直攒着。</summary>
    [TestMethod]
    public void SecondLateSecondaryForTheSameRequestIsAnOrphan()
    {
        var correlator = new TranscriptCorrelator();
        var request = correlator.RecordOutbound(Header(1, 1, true), 0x05, "<S1F1W />", T0);
        correlator.MarkTimedOut(0x05, T0.AddSeconds(1));

        var first = correlator.RecordInbound(Header(1, 2, false), 0x05, "S1F2", T0.AddSeconds(2));
        var second = correlator.RecordInbound(Header(1, 2, false), 0x05, "S1F2", T0.AddSeconds(3));

        Assert.AreEqual(RecordStatus.LateSecondary, first.Status);
        Assert.AreEqual(RecordStatus.OrphanSecondary, second.Status);
        Assert.AreEqual(RecordStatus.TimedOut, request.Status);
    }

    private static SecsHeader Header(int stream, int function, bool replyExpected, string? name = null) =>
        new(stream, function, replyExpected, name);
}

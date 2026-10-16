namespace QoderSecs.Core.Connection;

/// <summary>一条消息在通信记录里所处的阶段。含义随 <see cref="CommunicationRecord.Direction"/> 变化。</summary>
public enum RecordStatus
{
    /// <summary>发出的非 W 消息，或本端发出的回复。</summary>
    Sent,

    /// <summary>发出且需要 Secondary，尚未收到。</summary>
    AwaitingSecondary,

    /// <summary>发出的请求已经收到配对的 Secondary，<see cref="CommunicationRecord.ElapsedMilliseconds"/> 有值。</summary>
    SecondaryReceived,

    /// <summary>发出的请求 T3 超时。</summary>
    TimedOut,

    /// <summary>收到但不需要本端回复的消息。</summary>
    Received,

    /// <summary>收到且本端还未回复的 Primary；自动回复判定完就会变成下面两种之一。</summary>
    AwaitingReply,

    /// <summary>本端已回复的 Primary。v1 只有自动回复一条路径。</summary>
    Answered,

    /// <summary>收到带 W 的 Primary，但库里没给这条 SxFy 配 AutoReply，或配的目标发不出去。</summary>
    AutoReplySkipped,

    /// <summary>作为某个发出请求的 Secondary 收到。</summary>
    PairedSecondary,

    /// <summary>T3 超时之后才到达的 Secondary。</summary>
    LateSecondary,

    /// <summary>没有任何请求在等待的 Secondary。</summary>
    OrphanSecondary,

    /// <summary>连接断开时还挂着等待的记录：等的那条不会再来了。</summary>
    LinkClosed
}

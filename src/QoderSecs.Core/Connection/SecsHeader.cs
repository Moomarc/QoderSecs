namespace QoderSecs.Core.Connection;

/// <summary>一条 SECS 消息的头，与 Secs4Net 无关，便于单元测试。</summary>
public readonly record struct SecsHeader(int Stream, int Function, bool ReplyExpected, string? Name = null)
{
    /// <summary>SECS-II 里偶数 Function 是 Secondary（回复）。</summary>
    public bool IsSecondary => Function % 2 == 0;

    public string Signature => $"S{Stream}F{Function}{(ReplyExpected ? "W" : string.Empty)}";
}

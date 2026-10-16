namespace QoderSecs.Core.Connection;

public enum LinkPhase
{
    /// <summary>尚未连接。</summary>
    Offline,

    /// <summary>正在建立 HSMS 或等待 Select。</summary>
    Connecting,

    /// <summary>TCP 已连通但还没收到 SelectResponse。</summary>
    Connected,

    /// <summary>已 Select，可以收发 SECS 消息。</summary>
    Online,

    /// <summary>上过线之后对端断了。本端连接对象还在，槽位仍被占着，要手动断开才能重连。</summary>
    PeerLost,

    /// <summary>对端断开后 Secs4Net 正在重连。</summary>
    Retrying,

    /// <summary>正在主动断开。</summary>
    Disconnecting
}

public static class LinkPhaseExtensions
{
    public static bool IsLocked(this LinkPhase phase) => phase != LinkPhase.Offline;

    public static string Text(this LinkPhase phase) => phase switch
    {
        LinkPhase.Offline => "未连接",
        LinkPhase.Connecting => "连接中",
        LinkPhase.Connected => "已连通，等待 Select",
        LinkPhase.Online => "已连接",
        LinkPhase.PeerLost => "对方已断开",
        LinkPhase.Retrying => "重连中",
        LinkPhase.Disconnecting => "断开中",
        _ => phase.ToString()
    };
}

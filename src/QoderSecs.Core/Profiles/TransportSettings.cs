namespace QoderSecs.Core.Profiles;

/// <summary>这份连接参数走哪条传输。旧配置文件没有这个字段，按 HSMS 读。</summary>
public enum TransportKind
{
    Hsms,
    Secs1
}

public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space
}

public enum SerialStopBits
{
    One,
    OnePointFive,
    Two
}

public enum SerialFlowControl
{
    None,
    RtsCts,
    XOnXOff
}

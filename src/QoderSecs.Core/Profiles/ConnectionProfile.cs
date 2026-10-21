using System.Text.Json.Serialization;

namespace QoderSecs.Core.Profiles;

public sealed record ConnectionProfile
{
    public required string Id { get; init; }

    /// <summary>HSMS 或 SECS-I。老配置没有这个字段，默认走 HSMS。</summary>
    public TransportKind Transport { get; init; } = TransportKind.Hsms;

    public EndpointRole Role { get; init; }

    /// <summary>只对 HSMS 有意义：TCP 的 Active/Passive。SECS-I 是点对点，两边对等。</summary>
    public ConnectionMode Mode { get; init; }

    public required string IpAddress { get; init; }

    public int Port { get; init; }

    public int DeviceId { get; init; }

    /// <summary>等回复的超时。HSMS 和 SECS-I 共用这一档。</summary>
    public int T3Seconds { get; init; }

    public int T5Seconds { get; init; }

    public int T6Seconds { get; init; }

    public int T7Seconds { get; init; }

    public int T8Seconds { get; init; }

    /// <summary>SECS-I 块内字符间隔。</summary>
    public int T1Seconds { get; init; } = 1;

    /// <summary>SECS-I 握手一步的等待（发 ENQ 等 EOT、发块等 ACK）。</summary>
    public int T2Seconds { get; init; } = 15;

    /// <summary>SECS-I 回完 ACK 之后等下一块的时限。</summary>
    public int T4Seconds { get; init; } = 45;

    public string SerialPortName { get; init; } = "COM1";

    public int BaudRate { get; init; } = 9600;

    public int DataBits { get; init; } = 8;

    public SerialParity Parity { get; init; } = SerialParity.None;

    public SerialStopBits StopBits { get; init; } = SerialStopBits.One;

    public SerialFlowControl FlowControl { get; init; } = SerialFlowControl.None;

    public bool LinkTestEnabled { get; init; }

    public int LinkTestIntervalSeconds { get; init; }

    /// <summary>
    /// 界面上区分配置用的标签。配置没有名字：参数本身就是身份，
    /// 两个参数完全一样的配置本来也没有区分的必要。
    /// </summary>
    [JsonIgnore]
    public string Label => Transport == TransportKind.Secs1
        ? $"{Role} SECS-I {SerialPortName} {BaudRate}"
        : $"{Role} {Mode} {IpAddress}:{Port}";

    public static ConnectionProfile CreateDefault() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Transport = TransportKind.Hsms,
        Role = EndpointRole.Host,
        Mode = ConnectionMode.Active,
        IpAddress = "127.0.0.1",
        Port = 5000,
        DeviceId = 0,
        T3Seconds = 45,
        T5Seconds = 10,
        T6Seconds = 5,
        T7Seconds = 10,
        T8Seconds = 5,
        T1Seconds = 1,
        T2Seconds = 15,
        T4Seconds = 45,
        SerialPortName = "COM1",
        BaudRate = 9600,
        DataBits = 8,
        Parity = SerialParity.None,
        StopBits = SerialStopBits.One,
        FlowControl = SerialFlowControl.None,
        LinkTestEnabled = true,
        LinkTestIntervalSeconds = 30
    };
}

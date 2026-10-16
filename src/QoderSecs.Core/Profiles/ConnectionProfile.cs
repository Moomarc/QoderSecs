using System.Text.Json.Serialization;

namespace QoderSecs.Core.Profiles;

public sealed record ConnectionProfile
{
    public required string Id { get; init; }

    public EndpointRole Role { get; init; }

    public ConnectionMode Mode { get; init; }

    public required string IpAddress { get; init; }

    public int Port { get; init; }

    public int DeviceId { get; init; }

    public int T3Seconds { get; init; }

    public int T5Seconds { get; init; }

    public int T6Seconds { get; init; }

    public int T7Seconds { get; init; }

    public int T8Seconds { get; init; }

    public bool LinkTestEnabled { get; init; }

    public int LinkTestIntervalSeconds { get; init; }

    /// <summary>
    /// 界面上区分配置用的标签。配置没有名字：参数本身就是身份，
    /// 两个参数完全一样的配置本来也没有区分的必要。
    /// </summary>
    [JsonIgnore]
    public string Label => $"{Role} {Mode} {IpAddress}:{Port}";

    public static ConnectionProfile CreateDefault() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
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
        LinkTestEnabled = true,
        LinkTestIntervalSeconds = 30
    };
}

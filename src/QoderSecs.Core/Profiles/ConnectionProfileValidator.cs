using System.Net;

namespace QoderSecs.Core.Profiles;

public static class ConnectionProfileValidator
{
    public static IReadOnlyList<string> Validate(ConnectionProfile profile)
    {
        var errors = new List<string>();

        if (!Guid.TryParseExact(profile.Id, "N", out _))
        {
            errors.Add("配置 ID 无效。");
        }

        if (!IPAddress.TryParse(profile.IpAddress, out _))
        {
            errors.Add("IP 地址无效。");
        }

        if (profile.Port is < 1 or > 65535)
        {
            errors.Add("端口必须在 1 到 65535 之间。");
        }

        if (profile.DeviceId is < 0 or > 32767)
        {
            errors.Add("Device ID 必须在 0 到 32767 之间。");
        }

        ValidateTimer(profile.T3Seconds, "T3", errors);
        ValidateTimer(profile.T5Seconds, "T5", errors);
        ValidateTimer(profile.T6Seconds, "T6", errors);
        ValidateTimer(profile.T7Seconds, "T7", errors);
        ValidateTimer(profile.T8Seconds, "T8", errors);

        if (profile.LinkTestEnabled)
        {
            ValidateTimer(profile.LinkTestIntervalSeconds, "Linktest", errors);
        }

        return errors;
    }

    private static void ValidateTimer(int seconds, string name, ICollection<string> errors)
    {
        if (seconds is < 1 or > 86400)
        {
            errors.Add($"{name} 必须在 1 到 86400 秒之间。");
        }
    }
}

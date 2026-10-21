using System.Net;
using System.Text.RegularExpressions;

namespace QoderSecs.Core.Profiles;

public static partial class ConnectionProfileValidator
{
    public static IReadOnlyList<string> Validate(ConnectionProfile profile)
    {
        var errors = new List<string>();

        if (!Guid.TryParseExact(profile.Id, "N", out _))
        {
            errors.Add("配置 ID 无效。");
        }

        if (profile.DeviceId is < 0 or > 32767)
        {
            errors.Add("Device ID 必须在 0 到 32767 之间。");
        }

        ValidateTimer(profile.T3Seconds, "T3", errors);

        if (profile.Transport == TransportKind.Secs1)
        {
            ValidateSecs1(profile, errors);
        }
        else
        {
            ValidateHsms(profile, errors);
        }

        return errors;
    }

    private static void ValidateHsms(ConnectionProfile profile, List<string> errors)
    {
        if (!IPAddress.TryParse(profile.IpAddress, out _))
        {
            errors.Add("IP 地址无效。");
        }

        if (profile.Port is < 1 or > 65535)
        {
            errors.Add("端口必须在 1 到 65535 之间。");
        }

        ValidateTimer(profile.T5Seconds, "T5", errors);
        ValidateTimer(profile.T6Seconds, "T6", errors);
        ValidateTimer(profile.T7Seconds, "T7", errors);
        ValidateTimer(profile.T8Seconds, "T8", errors);

        if (profile.LinkTestEnabled)
        {
            ValidateTimer(profile.LinkTestIntervalSeconds, "Linktest", errors);
        }
    }

    /// <summary>
    /// SECS-I 只看串口和 T1/T2/T4。IP、端口、T5-T8 和 Linktest 是 HSMS 的字段，
    /// 切到串口时留着旧值不报错，切回去还能接着用。
    /// </summary>
    private static void ValidateSecs1(ConnectionProfile profile, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(profile.SerialPortName))
        {
            errors.Add("串口名不能为空。");
        }
        else if (!SerialPortNamePattern().IsMatch(profile.SerialPortName.Trim()))
        {
            errors.Add($"串口名「{profile.SerialPortName}」无效，应写成 COM1 这样的名字。");
        }

        if (profile.BaudRate is < 50 or > 4_000_000)
        {
            errors.Add("波特率必须在 50 到 4000000 之间。");
        }

        if (profile.DataBits is < 5 or > 8)
        {
            errors.Add("数据位必须在 5 到 8 之间。");
        }

        if (!Enum.IsDefined(profile.Parity))
        {
            errors.Add("校验位设置无效。");
        }

        if (!Enum.IsDefined(profile.StopBits))
        {
            errors.Add("停止位设置无效。");
        }

        if (!Enum.IsDefined(profile.FlowControl))
        {
            errors.Add("流控设置无效。");
        }

        ValidateTimer(profile.T1Seconds, "T1", errors);
        ValidateTimer(profile.T2Seconds, "T2", errors);
        ValidateTimer(profile.T4Seconds, "T4", errors);
    }

    private static void ValidateTimer(int seconds, string name, ICollection<string> errors)
    {
        if (seconds is < 1 or > 86400)
        {
            errors.Add($"{name} 必须在 1 到 86400 秒之间。");
        }
    }

    [GeneratedRegex(@"^COM[1-9]\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex SerialPortNamePattern();
}

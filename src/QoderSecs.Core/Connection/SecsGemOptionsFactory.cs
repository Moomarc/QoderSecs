using QoderSecs.Core.Profiles;
using Secs4Net;

namespace QoderSecs.Core.Connection;

public static class SecsGemOptionsFactory
{
    public static SecsGemOptions Create(ConnectionProfile profile)
    {
        var errors = ConnectionProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(profile));
        }

        return new SecsGemOptions
        {
            DeviceId = (ushort)profile.DeviceId,
            IpAddress = profile.IpAddress,
            Port = profile.Port,
            IsActive = profile.Mode == ConnectionMode.Active,
            T3 = SecondsToMilliseconds(profile.T3Seconds),
            T5 = SecondsToMilliseconds(profile.T5Seconds),
            T6 = SecondsToMilliseconds(profile.T6Seconds),
            T7 = SecondsToMilliseconds(profile.T7Seconds),
            T8 = SecondsToMilliseconds(profile.T8Seconds),
            LinkTestInterval = profile.LinkTestEnabled ? SecondsToMilliseconds(profile.LinkTestIntervalSeconds) : 0
        };
    }

    private static int SecondsToMilliseconds(int seconds) => seconds * 1000;
}

namespace QoderSecs.Core.Messages;

/// <summary>
/// 收到 Primary 时按库里配的 AutoReply 找该回哪条。v1 只有这一条来源：
/// 没配就不回，不内置 S1F1→S1F2 之类的规则，也不按 SxF(y+1) 推导。
/// </summary>
public static class AutoReplyResolver
{
    /// <summary>
    /// 目标为空或库里没有那条名字都返回 null。同一个 SxFy 有多条时取第一条配了 AutoReply 的。
    /// 名字按原样精确比：界面上的下拉框列的就是库里真实的 Name。
    /// </summary>
    public static MessageEntry? FindTarget(MessageLibrary library, int stream, int function)
    {
        ArgumentNullException.ThrowIfNull(library);

        var target = library.Messages
            .Where(entry => entry.Stream == stream && entry.Function == function)
            .Select(entry => entry.AutoReply?.Trim())
            .FirstOrDefault(name => !string.IsNullOrEmpty(name));

        return target is null
            ? null
            : library.Messages.FirstOrDefault(entry => string.Equals(entry.Name, target, StringComparison.Ordinal));
    }
}

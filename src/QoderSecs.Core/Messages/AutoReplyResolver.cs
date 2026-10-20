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

    /// <summary>
    /// 有哪些消息的 AutoReply 正指着这个名字。比名字用 Ordinal、两头去空格，
    /// 和 <see cref="FindTarget"/> 取目标时是同一条口径——改名和删除前后的提醒都从这一份出，
    /// 界面上不再自己数第二遍（数法不一致的话，提醒说没有、实际已经断了）。
    /// </summary>
    /// <param name="target">要被让出来的旧名字；空或空白时返回空列表（空目标等于没配）。</param>
    /// <param name="except">不算进来的那一条（删自己指自己的那条时用）。按 Index 认而不是按对象认：
    /// 改过一次的消息快照会换成新实例，认对象就把同一条当成两条了。</param>
    public static IReadOnlyList<MessageEntry> FindReferrers(
        MessageLibrary library,
        string? target,
        MessageEntry? except = null)
    {
        ArgumentNullException.ThrowIfNull(library);

        var name = target?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return [];
        }

        return library.Messages
            .Where(entry => except is null || entry.Index != except.Index)
            .Where(entry => string.Equals(entry.AutoReply?.Trim(), name, StringComparison.Ordinal))
            .ToList();
    }
}

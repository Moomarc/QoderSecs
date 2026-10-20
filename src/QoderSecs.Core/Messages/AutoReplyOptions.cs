namespace QoderSecs.Core.Messages;

/// <summary>
/// 自动回复下拉框里的一项。AutoReply 存的是目标消息的名字，但名字是人起的、
/// 看不出它回的是哪条 SxF，所以显示文字一律带上目标的 SxF。
/// </summary>
/// <param name="TargetName">写进 AutoReply 元素的那个名字；「不自动回复」这一项是 null。</param>
/// <param name="Display">下拉框那一行的文字。</param>
/// <param name="IsPaired">目标像不像本条的回复。只用于标注，发送仍按名字精确取。</param>
public sealed record AutoReplyOption(string? TargetName, string Display, bool IsPaired);

/// <summary>
/// 候选列表怎么生成、配对怎么判，都放在这里而不是界面里：
/// 下拉框只显示这份 Display，不再自己算第二遍。
/// </summary>
public static class AutoReplyOptions
{
    /// <summary>XML 里整个 AutoReply 元素不存在时，界面上就是这一项。</summary>
    public const string NoneText = "（不自动回复）";

    public static readonly AutoReplyOption None = new(null, NoneText, true);

    /// <summary>
    /// 不列自己（自动回复不能回给自己），也不列没有名字的消息
    /// （空名字的目标在 <see cref="AutoReplyResolver"/> 那边等于没配）。
    /// 库里没有的名字一律不列：改名和删除都会把指着旧名的引用置空
    /// （<see cref="MessageLibrary.SetName"/>、<see cref="MessageLibrary.RemoveMessage"/>），
    /// 所以下拉框里不该再冒出一条旧名字。
    /// 手改文件留下的失效引用同样不列，界面显示成「不自动回复」——运行时也确实不会回。
    /// </summary>
    public static IReadOnlyList<AutoReplyOption> Build(MessageLibrary library, MessageEntry? current)
    {
        ArgumentNullException.ThrowIfNull(library);

        var options = new List<AutoReplyOption> { None };
        foreach (var candidate in library.Query(null))
        {
            if (candidate.Name.Length == 0
                || (current is not null && candidate.Index == current.Index)
                || options.Any(option => option.TargetName == candidate.Name))
            {
                continue;
            }

            options.Add(For(candidate, current));
        }

        return options;
    }

    /// <summary>在当前这份候选里按存下来的名字找回那一项，找不到就落回「不自动回复」。</summary>
    public static AutoReplyOption Find(IReadOnlyList<AutoReplyOption> options, string? target)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.FirstOrDefault(option => option.TargetName == target?.Trim()) ?? None;
    }

    /// <summary>
    /// 回复的形状：同一条流、Function 比本条大 1（S12F1 的回复是 S12F2）。
    /// 这只是一句标注，程序不照它推导该回什么——v1 只按 AutoReply 里写的名字回，
    /// 不配对的那一条仍然可以选（SecsExpress 的库就是这么配的）。
    /// </summary>
    public static bool IsPairedWith(MessageEntry request, MessageEntry reply) =>
        reply.Stream == request.Stream && reply.Function == request.Function + 1;

    private static AutoReplyOption For(MessageEntry candidate, MessageEntry? current)
    {
        var text = $"{candidate.Signature} · {candidate.Name}";
        if (current is null)
        {
            return new AutoReplyOption(candidate.Name, text, true);
        }

        var isPaired = IsPairedWith(current, candidate);
        return new AutoReplyOption(candidate.Name, isPaired ? text : $"{text}（不配对）", isPaired);
    }
}

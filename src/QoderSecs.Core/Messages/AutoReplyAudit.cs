namespace QoderSecs.Core.Messages;

/// <summary>不一致的种类。界面只按这份枚举归类和测试，不靠解析提示文字认种类。</summary>
public enum AutoReplyIssueKind
{
    /// <summary>AutoReply 写了一个库里没有的名字。</summary>
    BrokenTarget,

    /// <summary>AutoReply 指向自己，回出去的就是收到的那条。</summary>
    SelfReference,

    /// <summary>同一个 SxFy 有多条配了 AutoReply，运行时只用文档里靠前的那条。</summary>
    Shadowed,

    /// <summary>目标名对应多条消息，取哪一条由文档顺序决定。</summary>
    DuplicateTargetName
}

public enum AutoReplyIssueSeverity
{
    Error,
    Warning
}

/// <summary>
/// 一条不一致。<see cref="Message"/> 是配置出问题的那条消息（被遮住的那条、
/// 写着断指的那条），不是它的目标；界面要跳到出问题的地方就按它定位。
/// </summary>
public sealed record AutoReplyIssue(
    AutoReplyIssueSeverity Severity,
    AutoReplyIssueKind Kind,
    MessageEntry Message,
    string Text);

/// <summary>一次检查的结果，顺序是错误在前、同级按消息在库里出现的先后。</summary>
public sealed record AutoReplyAuditReport(IReadOnlyList<AutoReplyIssue> Issues)
{
    public bool HasIssues => Issues.Count > 0;

    public int ErrorCount => Issues.Count(issue => issue.Severity == AutoReplyIssueSeverity.Error);

    public int WarningCount => Issues.Count(issue => issue.Severity == AutoReplyIssueSeverity.Warning);
}

/// <summary>
/// 整库扫一遍 AutoReply 配置。只读，不改任何文本——断指要清掉仍走
/// <see cref="MessageLibrary.SetAutoReply"/> 那条口径，不在这里另写一套。
///
/// 判据和运行时的 <see cref="AutoReplyResolver.FindTarget"/> 对齐：
/// 同一个 SxFy 取第一条配了 AutoReply 的，目标名两头去空格后按 Ordinal 精确匹配，
/// 同名的多条取文档里靠前的那条。检查报的就是运行时真正会发生的事，
/// 不按 SxF(y+1) 猜意图，也不把「W 位是 1 但没配 AutoReply」当成错误：
/// W 位说的是这条消息发出去要不要等回复，AutoReply 管的是收到同 SxFy 时回不回，
/// 两个方向，配不配是使用者的选择。
/// </summary>
public static class AutoReplyAudit
{
    public static AutoReplyAuditReport Run(MessageLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        var issues = new List<AutoReplyIssue>();
        var messages = library.Messages;

        var byName = messages
            .Where(entry => entry.Name.Length > 0)
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var entry in messages)
        {
            var target = entry.AutoReply?.Trim();
            if (string.IsNullOrEmpty(target))
            {
                continue;
            }

            if (!byName.TryGetValue(target, out var matches))
            {
                issues.Add(new AutoReplyIssue(
                    AutoReplyIssueSeverity.Error,
                    AutoReplyIssueKind.BrokenTarget,
                    entry,
                    $"自动回复目标「{target}」不在消息库中，运行时不会回复。"));
                continue;
            }

            var resolved = matches[0];
            if (resolved.Index == entry.Index)
            {
                issues.Add(new AutoReplyIssue(
                    AutoReplyIssueSeverity.Error,
                    AutoReplyIssueKind.SelfReference,
                    entry,
                    "自动回复指向自己，运行时会把这条当成回复发回去。"));
            }

            if (matches.Count > 1)
            {
                var signatures = string.Join("、", matches.Select(match => match.Signature));
                issues.Add(new AutoReplyIssue(
                    AutoReplyIssueSeverity.Warning,
                    AutoReplyIssueKind.DuplicateTargetName,
                    entry,
                    $"目标名「{target}」有 {matches.Count} 条消息同名（{signatures}），运行时取文档里靠前的那条。"));
            }
        }

        // 一个 SxFy 只认一条：FindTarget 挑出第一条配了 AutoReply 的名字就停，
        // 后面几条即使配了也永远不会被读到。这不是名字写错，改哪一条由使用者决定。
        foreach (var group in messages.GroupBy(entry => (entry.Stream, entry.Function)))
        {
            var configured = group
                .Where(entry => !string.IsNullOrWhiteSpace(entry.AutoReply))
                .ToList();
            if (configured.Count < 2)
            {
                continue;
            }

            var first = configured[0];
            var firstName = first.Name.Length == 0 ? "（无名）" : first.Name;
            foreach (var shadowed in configured.Skip(1))
            {
                issues.Add(new AutoReplyIssue(
                    AutoReplyIssueSeverity.Warning,
                    AutoReplyIssueKind.Shadowed,
                    shadowed,
                    $"同一 {shadowed.Signature} 有 {configured.Count} 条消息配了自动回复，"
                    + $"运行时只用文档里第一条「{firstName}」，这条不会生效。"));
            }
        }

        return new AutoReplyAuditReport(
            issues
                .OrderBy(issue => issue.Severity)
                .ThenBy(issue => issue.Message.Index)
                .ToList());
    }
}

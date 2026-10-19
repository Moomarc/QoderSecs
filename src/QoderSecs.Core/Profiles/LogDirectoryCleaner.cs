using System.Globalization;

namespace QoderSecs.Core.Profiles;

/// <summary>
/// 启动时收一次会话日志目录。每连接一次就建一个 <c>logs/&lt;配置Id&gt;/&lt;时间_进程号&gt;/</c>，
/// 程序以前从不删，一天开几十次攒得很快。口径是每个配置保留最近 20 个。
///
/// 只删程序自己建的那种目录名，别的目录一律不碰——有人会把日志拷进这个目录里存着。
/// 删不掉就跳过并计数：另一个实例可能正开着这一份 sml.log。
/// </summary>
public static class LogDirectoryCleaner
{
    /// <summary>每个配置保留最近多少个会话目录。</summary>
    public const int DefaultKeepCount = 20;

    public const int MinimumKeepCount = 1;

    /// <summary>清完了说一句；什么都没动就返回 null，不在状态栏占一行。</summary>
    public static string? Clean(string logsDirectory, int keepCount = DefaultKeepCount)
    {
        if (keepCount < MinimumKeepCount || !Directory.Exists(logsDirectory))
        {
            return null;
        }

        var deleted = 0;
        var skipped = 0;

        foreach (var profileDirectory in Enumerate(() => Directory.EnumerateDirectories(logsDirectory)))
        {
            var sessions = Enumerate(() => Directory.EnumerateDirectories(profileDirectory))
                .Select(path => (path, startedAt: TryReadSessionStart(Path.GetFileName(path))))
                .Where(session => session.startedAt is not null)
                .OrderByDescending(session => session.startedAt!.Value)
                .ThenByDescending(session => session.path, StringComparer.Ordinal)
                .Skip(keepCount)
                .ToList();

            foreach (var (path, _) in sessions)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    deleted++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 这份日志还在被写，或者权限不够。宁可留一堆没删干净的目录，
                    // 也不能把别人正在看的那份删掉。
                    skipped++;
                }
            }
        }

        if (deleted == 0 && skipped == 0)
        {
            return null;
        }

        var notice = deleted > 0
            ? $"已清理 {deleted} 个旧会话日志目录，每个配置保留最近 {keepCount} 个。"
            : string.Empty;
        if (skipped > 0)
        {
            notice += (deleted > 0 ? " " : string.Empty) + $"另有 {skipped} 个目录删不掉（可能正被别的实例占用），这次留着。";
        }

        return notice;
    }

    /// <summary>
    /// 目录名的前半截就是会话开始时间，和 AppDataPaths.CreateSessionDirectory 写的那一份对应：
    /// <c>yyyyMMdd_HHmmssfff_进程号</c>。时间戳自己就带一个下划线，所以要取前两段，
    /// 不能按第一个下划线切。第三段（进程号）也要求存在，
    /// 少一段就说明这不是程序建出来的目录。
    /// </summary>
    internal static DateTimeOffset? TryReadSessionStart(string directoryName)
    {
        var parts = directoryName.Split('_');
        if (parts.Length < 3)
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(
            $"{parts[0]}_{parts[1]}",
            "yyyyMMdd_HHmmssfff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var startedAt)
            ? startedAt
            : null;
    }

    /// <summary>
    /// 目录列举必须在 try 里就走完：EnumerateDirectories 返回的是延迟序列，
    /// 权限问题要等到第一次 MoveNext 才抛，那时候 try 已经退出去了。
    /// </summary>
    private static IReadOnlyList<string> Enumerate(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            return enumerate().ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

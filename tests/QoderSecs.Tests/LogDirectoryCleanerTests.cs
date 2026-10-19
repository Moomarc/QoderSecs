using QoderSecs.Core.Profiles;

namespace QoderSecs.Tests;

[TestClass]
public sealed class LogDirectoryCleanerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private string _logsDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _logsDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            "log-cleaner",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_logsDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_logsDirectory))
        {
            Directory.Delete(_logsDirectory, true);
        }
    }

    [TestMethod]
    public void KeepsTheNewestSessionsAndDeletesTheRest()
    {
        var profile = Guid.NewGuid().ToString("N");
        for (var index = 0; index < 25; index++)
        {
            CreateSession(profile, T0.AddMinutes(index));
        }

        var notice = LogDirectoryCleaner.Clean(_logsDirectory, keepCount: 20);

        Assert.AreEqual("已清理 5 个旧会话日志目录，每个配置保留最近 20 个。", notice);
        var remaining = Directory.GetDirectories(ProfileDirectory(profile));
        Assert.HasCount(20, remaining);
        CollectionAssert.DoesNotContain(remaining, SessionPath(profile, T0));
        CollectionAssert.Contains(remaining, SessionPath(profile, T0.AddMinutes(24)));
    }

    /// <summary>预算按配置算，不是全库算：一个配置连得多不会把另一个配置的日志挤掉。</summary>
    [TestMethod]
    public void EveryProfileGetsItsOwnBudget()
    {
        var first = Guid.NewGuid().ToString("N");
        var second = Guid.NewGuid().ToString("N");
        for (var index = 0; index < 4; index++)
        {
            CreateSession(first, T0.AddMinutes(index));
            CreateSession(second, T0.AddMinutes(index));
        }

        LogDirectoryCleaner.Clean(_logsDirectory, keepCount: 2);

        Assert.HasCount(2, Directory.GetDirectories(ProfileDirectory(first)));
        Assert.HasCount(2, Directory.GetDirectories(ProfileDirectory(second)));
    }

    [TestMethod]
    public void LeavesEverythingThatIsNotASessionDirectory()
    {
        var profile = Guid.NewGuid().ToString("N");
        for (var index = 0; index < 3; index++)
        {
            CreateSession(profile, T0.AddMinutes(index));
        }

        // 有人会把日志拷进这个目录存着；名字对不上程序那种写法的一律不认。
        var borrowed = Path.Combine(ProfileDirectory(profile), "从别处拷来的日志");
        var halfNamed = Path.Combine(ProfileDirectory(profile), "20261015_093000");
        Directory.CreateDirectory(borrowed);
        Directory.CreateDirectory(halfNamed);

        LogDirectoryCleaner.Clean(_logsDirectory, keepCount: 1);

        Assert.IsTrue(Directory.Exists(borrowed));
        Assert.IsTrue(Directory.Exists(halfNamed));
        Assert.IsTrue(Directory.Exists(SessionPath(profile, T0.AddMinutes(2))));
        Assert.IsFalse(Directory.Exists(SessionPath(profile, T0)));
        Assert.IsFalse(Directory.Exists(SessionPath(profile, T0.AddMinutes(1))));
        Assert.HasCount(3, Directory.GetDirectories(ProfileDirectory(profile)));
    }

    [TestMethod]
    public void SaysNothingWhenThereIsNothingToDelete()
    {
        var profile = Guid.NewGuid().ToString("N");
        CreateSession(profile, T0);
        CreateSession(profile, T0.AddMinutes(1));

        Assert.IsNull(LogDirectoryCleaner.Clean(_logsDirectory, keepCount: 20));
    }

    [TestMethod]
    public void SurvivesAMissingLogsDirectory()
    {
        Assert.IsNull(LogDirectoryCleaner.Clean(Path.Combine(_logsDirectory, "不存在"), keepCount: 20));
    }

    [TestMethod]
    public void AZeroKeepCountMeansDontTouchAnything()
    {
        var profile = Guid.NewGuid().ToString("N");
        for (var index = 0; index < 3; index++)
        {
            CreateSession(profile, T0.AddMinutes(index));
        }

        Assert.IsNull(LogDirectoryCleaner.Clean(_logsDirectory, keepCount: 0));
        Assert.HasCount(3, Directory.GetDirectories(ProfileDirectory(profile)));
    }

    /// <summary>另一个实例还开着这份日志，删不动就留着，不能把启动卡住。</summary>
    [TestMethod]
    public void ReportsTheDirectoriesItCouldNotDelete()
    {
        var profile = Guid.NewGuid().ToString("N");
        for (var index = 0; index < 4; index++)
        {
            CreateSession(profile, T0.AddMinutes(index));
        }

        var lockedLog = Path.Combine(SessionPath(profile, T0), "sml.log");
        using (new FileStream(lockedLog, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var notice = LogDirectoryCleaner.Clean(_logsDirectory, keepCount: 1);

            StringAssert.Contains(notice!, "删不掉");
            Assert.IsTrue(Directory.Exists(SessionPath(profile, T0)));
            Assert.IsTrue(Directory.Exists(SessionPath(profile, T0.AddMinutes(3))));
        }
    }

    [TestMethod]
    public void ReadsOnlyTheExactSessionDirectoryName()
    {
        var startedAt = LogDirectoryCleaner.TryReadSessionStart("20261015_093000123_4242");

        Assert.IsNotNull(startedAt);
        // 目录名里不带时区，TryParseExact 按本机时区解释；新旧先后只比同一口径的相对顺序，
        // 所以这里只核对它把哪一段时间读进去了。
        Assert.AreEqual("2026-10-15 09:30:00.123", startedAt.Value.DateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        Assert.IsNull(LogDirectoryCleaner.TryReadSessionStart("20261015_093000"));
        Assert.IsNull(LogDirectoryCleaner.TryReadSessionStart("logs_2026_1015"));
        Assert.IsNull(LogDirectoryCleaner.TryReadSessionStart("备份"));
    }

    private string ProfileDirectory(string profileId) => Path.Combine(_logsDirectory, profileId);

    private string SessionPath(string profileId, DateTimeOffset startedAt) =>
        Path.Combine(ProfileDirectory(profileId), $"{startedAt:yyyyMMdd_HHmmssfff}_{Environment.ProcessId}");

    private void CreateSession(string profileId, DateTimeOffset startedAt)
    {
        var directory = SessionPath(profileId, startedAt);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "sml.log"), "S1F1");
    }
}

using QoderSecs.Core.Profiles;

namespace QoderSecs.Tests;

[TestClass]
public sealed class AppDataPathsTests
{
    private string _testDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    public void CreatesPortableDataAndSessionDirectories()
    {
        var paths = new AppDataPaths(_testDirectory);
        paths.EnsureCreated();
        var profileId = Guid.NewGuid().ToString("N");

        var sessionDirectory = paths.CreateSessionDirectory(
            profileId,
            new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero),
            42);

        Assert.IsTrue(Directory.Exists(paths.DataDirectory));
        Assert.IsTrue(Directory.Exists(paths.LogsDirectory));
        Assert.IsTrue(Directory.Exists(sessionDirectory));
        StringAssert.Contains(sessionDirectory, profileId);
        StringAssert.EndsWith(sessionDirectory, "20261015_093000000_42");
        StringAssert.EndsWith(paths.SettingsPath,
            Path.Combine(AppDataPaths.DataDirectoryName, "settings.json"));
    }

    [TestMethod]
    public void TwoSessionsInTheSameSecondGetDifferentDirectories()
    {
        var paths = new AppDataPaths(_testDirectory);
        var profileId = Guid.NewGuid().ToString("N");
        var first = new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);

        var early = paths.CreateSessionDirectory(profileId, first, 42);
        var late = paths.CreateSessionDirectory(profileId, first.AddMilliseconds(500), 42);

        Assert.AreNotEqual(early, late, "同一秒内断开重连不能写进同一个会话目录");
    }

    [TestMethod]
    public void RenamesLegacyDataDirectoryAndTellsTheUser()
    {
        var legacy = Path.Combine(_testDirectory, "data");
        Directory.CreateDirectory(Path.Combine(legacy, "logs"));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

        var paths = new AppDataPaths(_testDirectory);
        var notice = paths.EnsureCreated();

        Assert.IsNotNull(notice, "搬了老目录得给界面一句话，不能让人以为日志凭空没了");
        Assert.IsFalse(Directory.Exists(legacy), "老目录是改名搬走，不是复制一份留两份");
        Assert.IsTrue(File.Exists(Path.Combine(paths.DataDirectory, "settings.json")), "老参数要原样搬过来");
        Assert.IsTrue(Directory.Exists(paths.LogsDirectory), "老日志目录要跟着一起搬过来");
    }

    [TestMethod]
    public void SaysNothingWhenThereIsNoLegacyDirectory()
    {
        var paths = new AppDataPaths(_testDirectory);

        Assert.IsNull(paths.EnsureCreated(), "正常启动不该冒出目录提示");
        Assert.IsFalse(Directory.Exists(paths.LegacyDataDirectory));
    }

    [TestMethod]
    public void LeavesBothDirectoriesAloneWhenTheNewOneAlreadyExists()
    {
        var legacy = Path.Combine(_testDirectory, "data");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "旧内容");

        var paths = new AppDataPaths(_testDirectory);
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(paths.SettingsPath, "新内容");

        Assert.IsNull(paths.EnsureCreated(), "新目录已经在使用了，不该再去动老的");
        Assert.AreEqual("旧内容", File.ReadAllText(Path.Combine(legacy, "settings.json")),
            "两边都在时不能覆盖任何一边");
        Assert.AreEqual("新内容", File.ReadAllText(paths.SettingsPath));
    }

    [TestMethod]
    public void SampleLibraryLivesInTheDataDirectory()
    {
        var paths = new AppDataPaths(_testDirectory);

        // 导入对话框停在哪儿、启动时往哪儿解，都只看这一份路径，不能各算各的。
        Assert.AreEqual(paths.DataDirectory, paths.SampleDirectory);

        // 样本和 settings.json 并排，不再单独占一个 samples 文件夹；
        // 解包按这些文件名去找内嵌资源，它们和 csproj 里的文件对不上就会静默少解一份。
        Assert.AreEqual(
            Path.Combine(paths.DataDirectory, AppDataPaths.RichSampleFileName),
            paths.SampleFilePath);
        Assert.AreEqual(
            Path.Combine(paths.DataDirectory, AppDataPaths.RawSampleFileName),
            paths.RawSampleFilePath);
    }
}
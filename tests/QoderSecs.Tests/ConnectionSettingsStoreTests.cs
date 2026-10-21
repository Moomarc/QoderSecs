using QoderSecs.Core.Profiles;

namespace QoderSecs.Tests;

[TestClass]
public sealed class ConnectionSettingsStoreTests
{
    private string _testDirectory = null!;
    private string _settingsPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "test-data",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
        _settingsPath = Path.Combine(_testDirectory, "settings.json");
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
    public void FreshProfileDefaultsToHsms()
    {
        // SECS-I 加了入口之后 HSMS 仍是默认和主模式，这一条防止有人把默认值顺手改掉。
        Assert.AreEqual(TransportKind.Hsms, ConnectionProfile.CreateDefault().Transport);
    }

    [TestMethod]
    public async Task SaveAndLoadRoundTripsTheOneProfile()
    {
        var store = new ConnectionSettingsStore(_settingsPath);
        var profile = ConnectionProfile.CreateDefault() with
        {
            Role = EndpointRole.Equipment,
            Mode = ConnectionMode.Passive,
            Port = 6200,
            DeviceId = 12
        };

        await store.SaveAsync(profile);
        var loaded = await store.LoadAsync();

        Assert.IsNull(loaded.Problem);
        Assert.AreEqual(profile, loaded.Profile);
        Assert.AreEqual(0, Directory.EnumerateFiles(_testDirectory, "*.tmp").Count(), "临时文件必须被替换掉，不能留下");
    }

    [TestMethod]
    public async Task SaveRejectsInvalidProfile()
    {
        var store = new ConnectionSettingsStore(_settingsPath);
        var profile = ConnectionProfile.CreateDefault() with
        {
            IpAddress = "not-an-ip",
            Port = 0
        };

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => store.SaveAsync(profile));
        Assert.IsFalse(File.Exists(_settingsPath), "存不进去就不该留下半个文件");
    }

    /// <summary>第一次运行没有这个文件：不报错，也不新建，直接给一份能用的默认参数。</summary>
    [TestMethod]
    public async Task MissingFileGivesDefaultsWithoutComplaining()
    {
        var loaded = await new ConnectionSettingsStore(_settingsPath).LoadAsync();

        Assert.IsNull(loaded.Problem);
        Assert.AreEqual(ConnectionProfile.CreateDefault() with { Id = loaded.Profile.Id }, loaded.Profile);
        Assert.IsFalse(File.Exists(_settingsPath));
    }

    /// <summary>文件坏了不能让人连不上机台：退回默认值，同时把原因说出来。</summary>
    [TestMethod]
    public async Task UnreadableFileFallsBackToDefaultsAndSaysSo()
    {
        await File.WriteAllTextAsync(_settingsPath, "{ 这不是 JSON");

        var loaded = await new ConnectionSettingsStore(_settingsPath).LoadAsync();

        Assert.AreEqual(ConnectionProfile.CreateDefault() with { Id = loaded.Profile.Id }, loaded.Profile);
        Assert.IsNotNull(loaded.Problem);
        StringAssert.Contains(loaded.Problem, "默认值");
    }

    [TestMethod]
    public async Task OutOfRangeValuesFallBackToDefaultsAndSaySo()
    {
        await File.WriteAllTextAsync(_settingsPath, """
            {"Id":"0a5050a0a0a0a0a0a0a0a0a0a0a00001","Role":"Host","Mode":"Active",
             "IpAddress":"127.0.0.1","Port":70000,"DeviceId":0,
             "T3Seconds":45,"T5Seconds":10,"T6Seconds":5,"T7Seconds":10,"T8Seconds":5,
             "LinkTestEnabled":false,"LinkTestIntervalSeconds":30}
            """);

        var loaded = await new ConnectionSettingsStore(_settingsPath).LoadAsync();

        Assert.IsNotNull(loaded.Problem);
        StringAssert.Contains(loaded.Problem, "端口");
        Assert.AreEqual(5000, loaded.Profile.Port);
    }

    /// <summary>文件里多出已经不认识的字段（比如以前的 Name）时，读的时候要当没看见。</summary>
    [TestMethod]
    public async Task LoadIgnoresFieldsItNoLongerKnows()
    {
        var store = new ConnectionSettingsStore(_settingsPath);
        var profile = ConnectionProfile.CreateDefault();
        await store.SaveAsync(profile);

        var json = await File.ReadAllTextAsync(_settingsPath);
        await File.WriteAllTextAsync(_settingsPath, json.Replace("\"Id\":", "\"Name\": \"HOST-测试\",\n  \"Id\":", StringComparison.Ordinal));

        var loaded = await store.LoadAsync();

        Assert.IsNull(loaded.Problem);
        Assert.AreEqual(profile, loaded.Profile);
    }

    [TestMethod]
    public void ValidatorChecksSecsAndNetworkRanges()
    {
        var profile = ConnectionProfile.CreateDefault() with
        {
            DeviceId = 32768,
            T3Seconds = 0,
            LinkTestIntervalSeconds = 86401
        };

        var errors = ConnectionProfileValidator.Validate(profile);

        Assert.HasCount(3, errors);
    }

    /// <summary>参数没有名字，界面上靠参数拼出来的标签区分。</summary>
    [TestMethod]
    public void LabelIdentifiesAProfileByItsParameters()
    {
        var profile = ConnectionProfile.CreateDefault() with
        {
            Role = EndpointRole.Equipment,
            Mode = ConnectionMode.Passive,
            Port = 5050
        };

        Assert.AreEqual("Equipment Passive 127.0.0.1:5050", profile.Label);
    }
}

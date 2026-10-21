using System.Text.Json;
using System.Text.RegularExpressions;
using QoderSecs.Core.Connection;
using QoderSecs.Core.Profiles;

namespace QoderSecs.Tests;

/// <summary>
/// 会话单元负责三种日志的开头：sml.log 的会话头、detail.log 的首行、effective-profile.json 的会话信息，
/// 三处必须是同一个开始时刻、同一个进程和同一种传输方式，事后才能按时间对齐。
/// </summary>
[TestClass]
public sealed class SecsSessionTests
{
    [TestMethod]
    public async Task OpenWritesOneSessionStartIntoAllThreeLogs()
    {
        var directory = NewSessionDirectory();
        var profile = ConnectionProfile.CreateDefault();
        try
        {
            var session = new SecsSession { SessionDirectoryFactory = _ => directory };
            session.Open(profile, "HSMS", "测试会话");

            StringAssert.Contains(session.Log!.SmlLogPath, directory);
            await session.CloseAsync();

            var smlHeader = (await File.ReadAllLinesAsync(Path.Combine(directory, "sml.log")))[0];
            StringAssert.StartsWith(smlHeader, "# QoderSecs session started=");
            StringAssert.Contains(smlHeader, $"pid={Environment.ProcessId}");
            StringAssert.Contains(smlHeader, $"profile={profile.Id}");
            StringAssert.Contains(smlHeader, "transport=HSMS");

            var detailLines = await File.ReadAllLinesAsync(Path.Combine(directory, "detail.log"));
            StringAssert.Matches(
                detailLines[0],
                new Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d\d\d "));
            StringAssert.EndsWith(detailLines[0], " 会话开始 测试会话");
            Assert.AreEqual("会话结束", detailLines[^1][24..]);

            using var snapshot = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(directory, "effective-profile.json")));
            var root = snapshot.RootElement;
            Assert.AreEqual("HSMS", root.GetProperty("Transport").GetString());
            Assert.AreEqual(Environment.ProcessId, root.GetProperty("ProcessId").GetInt32());
            Assert.AreEqual(profile.Id, root.GetProperty("Profile").GetProperty("Id").GetString());

            var startedAt = root.GetProperty("StartedAt").GetDateTimeOffset();
            var smlStartedAt = DateTimeOffset.Parse(
                smlHeader["# QoderSecs session started=".Length..].Split(' ')[0]);
            Assert.AreEqual(0.0, (startedAt - smlStartedAt).Duration().TotalSeconds, 1.0,
                "三个文件里的开始时刻必须落在同一秒内。");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [TestMethod]
    public async Task DetailLinesCarryTheSequenceThatLocatesTheSmlBlock()
    {
        var directory = NewSessionDirectory();
        try
        {
            var session = new SecsSession { SessionDirectoryFactory = _ => directory };
            session.Open(ConnectionProfile.CreateDefault(), "HSMS", "测试会话");

            using var message = new Secs4Net.SecsMessage(1, 1, replyExpected: false);
            var record = session.Record(SecsDirection.Out, 7, message);
            await session.CloseAsync();

            var detail = await File.ReadAllTextAsync(Path.Combine(directory, "detail.log"));
            StringAssert.Contains(detail, $"seq={record.SmlSequence} 状态=已发送");

            var smlIndex = (await File.ReadAllLinesAsync(Path.Combine(directory, "sml.log")))
                .Single(line => line.Contains("# seq=", StringComparison.Ordinal));
            StringAssert.Contains(smlIndex, "sys=00000007 dir=Out S1F1");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static string NewSessionDirectory() =>
        Path.Combine(Path.GetTempPath(), "qodersecs-session-tests", Guid.NewGuid().ToString("N"));
}

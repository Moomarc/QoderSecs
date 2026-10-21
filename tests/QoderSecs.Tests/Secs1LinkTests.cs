using System.Collections.Concurrent;
using QoderSecs.Core.Connection;
using QoderSecs.Core.Messages;
using QoderSecs.Core.Profiles;
using Secs4Net;

namespace QoderSecs.Tests;

/// <summary>
/// SECS-I 会话层：两条 <see cref="Secs1Link"/> 用一对内存管道背靠背跑完整条握手线，
/// 验记录、SystemBytes 配对、自动回复、T3 超时和取消等待。没有串口硬件也能跑。
/// </summary>
[TestClass]
public sealed class Secs1LinkTests
{
    /// <summary>串口那头没了（对端关端口或 USB 转接头被拔）要把阶段改成「对方断开」，不能一直挂在「在线」。</summary>
    [TestMethod]
    public async Task StreamEndingMarksTheLinkAsPeerLost()
    {
        var (hostStream, _) = PipeStream.CreatePair();
        var directory = NewSessionDirectory();
        try
        {
            await using var host = new Secs1Link(Session(directory)) { StreamFactory = _ => hostStream };
            host.Connect(Profile(isEquipment: false));
            await WaitForAsync(() => host.Phase == LinkPhase.Online, "没上线");

            hostStream.DeliverEnd();

            await WaitForAsync(() => host.Phase == LinkPhase.PeerLost, "线路结束后没报对方断开");
            var detail = await ReadDetailLogAsync(directory);
            StringAssert.Contains(detail, "串口不再有数据");
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    [TestMethod]
    public async Task AutoReplyComesBackOverTheLineAndBothSidesAreRecorded()
    {
        var (hostStream, eqpStream) = PipeStream.CreatePair();
        var hostDirectory = NewSessionDirectory();
        var eqpDirectory = NewSessionDirectory();
        try
        {
            await using var host = new Secs1Link(Session(hostDirectory)) { StreamFactory = _ => hostStream };
            await using var eqp = new Secs1Link(Session(eqpDirectory)) { StreamFactory = _ => eqpStream };
            eqp.LibraryProvider = SampleLibrary;

            host.Connect(Profile(isEquipment: false));
            eqp.Connect(Profile(isEquipment: true));
            await WaitForAsync(
                () => host.Phase == LinkPhase.Online && eqp.Phase == LinkPhase.Online,
                "两端没上线");

            using var request = new SecsMessage(1, 1, replyExpected: true);
            var outcome = await host.SendAsync(request, TestTimeout());

            StringAssert.StartsWith(outcome, "已发出 S1F1W，收到 S1F2");

            var outbound = host.Transcript.Records.Single(record => record.Direction == SecsDirection.Out);
            var inbound = host.Transcript.Records.Single(record => record.Direction == SecsDirection.In);
            Assert.AreEqual(RecordStatus.SecondaryReceived, outbound.Status);
            Assert.AreEqual(outbound.SystemBytes, inbound.SystemBytes);
            Assert.AreEqual(RecordStatus.PairedSecondary, inbound.Status);

            // 对端把回复写完才会把那条入站记录从 AwaitingReply 改成 Answered，
            // 这个动作可能比本端收到 Secondary 晚一线，等一下再读记录。
            await WaitForAsync(
                () => eqp.Transcript.Records.Any(record =>
                    record.Direction == SecsDirection.In
                    && record.Status == RecordStatus.Answered),
                "自动回复没记成已回答");
            var answered = eqp.Transcript.Records.Single(record => record.Direction == SecsDirection.In);
            Assert.AreEqual(RecordStatus.Answered, answered.Status);
            Assert.AreEqual("S1F1W", answered.Signature);

            var detail = await ReadDetailLogAsync(eqpDirectory);
            StringAssert.Contains(detail, "自动回复 收到 S1F1W");
            StringAssert.Contains(detail, "串口=COM1");
            var smlHeader = (await ReadLogLinesAsync(Path.Combine(eqpDirectory, "sml.log")))[0];
            StringAssert.Contains(smlHeader, "transport=SECS-I");
        }
        finally
        {
            DeleteQuietly(hostDirectory);
            DeleteQuietly(eqpDirectory);
        }
    }

    [TestMethod]
    public async Task PrimaryWithoutAutoReplyTimesOutAtT3()
    {
        var (hostStream, eqpStream) = PipeStream.CreatePair();
        var directory = NewSessionDirectory();
        try
        {
            await using var host = new Secs1Link(Session(directory)) { StreamFactory = _ => hostStream };
            await using var eqp = new Secs1Link(Session(directory + "-eqp")) { StreamFactory = _ => eqpStream };

            host.Connect(Profile(isEquipment: false, t3: 1));
            eqp.Connect(Profile(isEquipment: true, t3: 1));
            await WaitForAsync(
                () => host.Phase == LinkPhase.Online && eqp.Phase == LinkPhase.Online,
                "两端没上线");

            using var request = new SecsMessage(1, 1, replyExpected: true);
            var started = DateTimeOffset.Now;
            var outcome = await host.SendAsync(request, TestTimeout());

            StringAssert.Contains(outcome, "等待回复超时（T3 1 秒）");
            Assert.IsGreaterThanOrEqualTo(900.0, (DateTimeOffset.Now - started).TotalMilliseconds);
            Assert.AreEqual(RecordStatus.TimedOut, host.Transcript.Records.Last().Status);
        }
        finally
        {
            DeleteQuietly(directory);
            DeleteQuietly(directory + "-eqp");
        }
    }

    [TestMethod]
    public async Task CancellingTheWaitKeepsTheSentRecordAndReleasesImmediately()
    {
        var (hostStream, eqpStream) = PipeStream.CreatePair();
        var directory = NewSessionDirectory();
        try
        {
            await using var host = new Secs1Link(Session(directory)) { StreamFactory = _ => hostStream };
            await using var eqp = new Secs1Link(Session(directory + "-eqp")) { StreamFactory = _ => eqpStream };
            var hostRecords = Watch(host);

            host.Connect(Profile(isEquipment: false, t3: 30));
            eqp.Connect(Profile(isEquipment: true, t3: 30));
            await WaitForAsync(
                () => host.Phase == LinkPhase.Online && eqp.Phase == LinkPhase.Online,
                "两端没上线");

            using var request = new SecsMessage(1, 1, replyExpected: true);
            var sending = host.SendAsync(request, TestTimeout());

            // 等对端确实收到这条消息，才说明发送已经完成、现在停在等回复。
            await WaitForAsync(
                () => eqp.Transcript.Records.Any(record => record.Direction == SecsDirection.In),
                "对端没收到请求");

            Assert.IsTrue(host.CancelPendingSend(), "这条发送应该正等着回复");
            var outcome = await sending;

            StringAssert.Contains(outcome, "已取消等待回复");
            var sent = hostRecords.Single(record => record.Direction == SecsDirection.Out);
            Assert.AreEqual(RecordStatus.WaitAbandoned, sent.Status);
        }
        finally
        {
            DeleteQuietly(directory);
            DeleteQuietly(directory + "-eqp");
        }
    }

    /// <summary>
    /// 界面那层只认 ConnectionLink：参数里写 SECS-I 就该挑串口那条路，
    /// 记录仍然落在同一个关联器上，切传输方式不用重订阅。
    /// </summary>
    [TestMethod]
    public async Task ConnectionLinkRoutesSecs1ProfilesAndKeepsOneRecordList()
    {
        var (hostStream, eqpStream) = PipeStream.CreatePair();
        var directory = NewSessionDirectory();
        try
        {
            await using var host = new ConnectionLink
            {
                SessionDirectoryFactory = _ => directory,
                Secs1StreamFactory = _ => hostStream
            };
            await using var eqp = new ConnectionLink
            {
                SessionDirectoryFactory = _ => directory + "-eqp",
                Secs1StreamFactory = _ => eqpStream
            };
            eqp.LibraryProvider = SampleLibrary;

            host.Connect(Profile(isEquipment: false));
            eqp.Connect(Profile(isEquipment: true));
            await WaitForAsync(
                () => host.Phase == LinkPhase.Online && eqp.Phase == LinkPhase.Online,
                "两端没上线");

            using var request = new SecsMessage(1, 1, replyExpected: true);
            StringAssert.StartsWith(await host.SendAsync(request, TestTimeout()), ConnectionLink.SentPrefix);
            Assert.HasCount(2, host.Transcript.Records);
            Assert.AreEqual("S1F2", host.Transcript.Records[1].Signature);
        }
        finally
        {
            DeleteQuietly(directory);
            DeleteQuietly(directory + "-eqp");
        }
    }

    [TestMethod]
    public void Secs1ProfileValidationChecksSerialParametersOnly()
    {
        var valid = Profile(isEquipment: false);
        Assert.IsEmpty(ConnectionProfileValidator.Validate(valid));

        var badName = ConnectionProfileValidator.Validate(valid with { SerialPortName = "串口一" });
        StringAssert.Contains(badName.Single(), "串口名");

        var badTimer = ConnectionProfileValidator.Validate(valid with { T2Seconds = 0 });
        StringAssert.Contains(badTimer.Single(), "T2");

        // HSMS 的 IP 和端口在 SECS-I 下不参与判断，留着旧值不该拦住连接。
        var staleHsmsFields = ConnectionProfileValidator.Validate(
            valid with { IpAddress = "not-an-ip", Port = 0 });
        Assert.IsEmpty(staleHsmsFields);
    }

    private static SecsSession Session(string directory) =>
        new() { SessionDirectoryFactory = _ => directory };

    private static ConnectionProfile Profile(bool isEquipment, int t3 = 5) =>
        ConnectionProfile.CreateDefault() with
        {
            Transport = TransportKind.Secs1,
            Role = isEquipment ? EndpointRole.Equipment : EndpointRole.Host,
            Mode = ConnectionMode.Active,
            SerialPortName = "COM1",
            T1Seconds = 1,
            T2Seconds = 2,
            T3Seconds = t3,
            T4Seconds = 3,
            DeviceId = 1,
            LinkTestEnabled = false
        };

    private static MessageLibrary SampleLibrary() => MessageLibrary.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-library.sxml")));

    private static BlockingCollection<CommunicationRecord> Watch(Secs1Link link)
    {
        var sink = new BlockingCollection<CommunicationRecord>();
        link.Transcript.RecordAdded += record => sink.Add(record);
        return sink;
    }

    private static CancellationToken TestTimeout() => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new AssertFailedException($"{what}（等了 5 秒）。");
    }

    private static async Task<string> ReadDetailLogAsync(string directory) =>
        string.Join("\n", await ReadLogLinesAsync(Path.Combine(directory, "detail.log")));

    private static async Task<string[]> ReadLogLinesAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return (await reader.ReadToEndAsync()).Split('\n');
    }

    private static string NewSessionDirectory() =>
        Path.Combine(Path.GetTempPath(), "qodersecs-secs1-link-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteQuietly(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

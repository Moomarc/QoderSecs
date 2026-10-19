using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using QoderSecs.Core.Connection;
using QoderSecs.Core.Messages;
using QoderSecs.Core.Profiles;
using Secs4Net;

namespace QoderSecs.Tests;

[TestClass]
public sealed class HsmsLinkLoopbackTests
{
    [TestMethod]
    public async Task PassiveAndActiveLinksBothReachOnline()
    {
        var port = ReservePort();
        await using var server = new HsmsLink();
        await using var client = new HsmsLink();

        server.Connect(Passive(port));
        client.Connect(Active(port));

        await WaitFor("服务端", () => server.Phase == LinkPhase.Online);
        await WaitFor("客户端", () => client.Phase == LinkPhase.Online);
        Assert.AreEqual(LinkPhase.Online, server.Phase);
        Assert.AreEqual(LinkPhase.Online, client.Phase);

        await client.DisconnectAsync();

        Assert.AreEqual(LinkPhase.Offline, client.Phase);
        Assert.IsTrue(client.CanConnect);
        await WaitFor("服务端离开 Online", () => server.Phase != LinkPhase.Online);
    }

    /// <summary>
    /// 对端消失后不能停在"连接中"：这条连接其实已经废了，槽位还占着，界面要说清楚。
    /// 对端回来以后还要能自己恢复成已连接。
    /// </summary>
    [TestMethod]
    public async Task PeerDisappearingIsReportedAsPeerLostAndRecovers()
    {
        var port = ReservePort();
        await using var server = new HsmsLink();
        await using var client = new HsmsLink();

        server.Connect(Passive(port));
        client.Connect(Active(port));
        await WaitFor("两端就绪", () => server.Phase == LinkPhase.Online && client.Phase == LinkPhase.Online);

        await client.DisconnectAsync();

        await WaitFor("服务端认出对端已断开", () => server.Phase == LinkPhase.PeerLost);
        Assert.AreEqual("对方已断开", server.Phase.Text());
        Assert.IsTrue(server.Phase.IsLocked(), "槽位还占着，按钮必须还是「断开」");
        Assert.IsFalse(server.CanConnect);

        client.Connect(Active(port));

        await WaitFor("对端回来后重新就绪", () => server.Phase == LinkPhase.Online && client.Phase == LinkPhase.Online);

        await server.DisconnectAsync();

        await WaitFor("客户端认出对端已断开", () => client.Phase == LinkPhase.PeerLost);
        Assert.AreEqual(LinkPhase.Offline, server.Phase);
    }

    /// <summary>界面复用同一个 HsmsLink 实例，断开后必须能再次连接。</summary>
    [TestMethod]
    public async Task TheSameLinkCanBeConnectedAgainAfterDisconnect()
    {
        var port = ReservePort();
        await using var server = new HsmsLink();
        await using var client = new HsmsLink();

        server.Connect(Passive(port));
        client.Connect(Active(port));
        await WaitFor("第一次就绪", () => server.Phase == LinkPhase.Online && client.Phase == LinkPhase.Online);

        await client.DisconnectAsync();

        Assert.AreEqual(LinkPhase.Offline, client.Phase);
        Assert.IsTrue(client.CanConnect);

        client.Connect(Active(port));

        await WaitFor("重连后再次就绪", () => client.Phase == LinkPhase.Online);
        Assert.IsFalse(client.CanConnect);
    }

    [TestMethod]
    public async Task PeerPrimaryWithoutWBitBecomesReceivedRecord()
    {
        await using var server = new HsmsLink();
        var records = Watch(server);
        server.Connect(Passive(ReservePort()));

        await using var peer = await SecsPeer.ConnectAsync("127.0.0.1", PortOf(server));
        await peer.SendAsync(new SecsMessage(2, 41, replyExpected: false)
        {
            Name = "REMOTE-STATE",
            SecsItem = Item.L(Item.U4(1))
        });

        var record = await TakeAsync(records);
        Assert.AreEqual(SecsDirection.In, record.Direction);
        Assert.AreEqual("S2F41", record.Signature);
        Assert.AreEqual(RecordStatus.Received, record.Status);
        StringAssert.Contains(record.Sml, "<U4");
        StringAssert.Contains(record.Sml, "S2F41");
    }

    /// <summary>自动回复默认不开启：没挂消息库就不回，记录要落到未自动回复，不能一直挂着待本端回复。</summary>
    [TestMethod]
    public async Task PeerPrimaryWithoutALibraryIsNotAutoReplied()
    {
        await using var server = new HsmsLink();
        var records = Watch(server);
        server.Connect(Passive(ReservePort()));

        await using var peer = await SecsPeer.ConnectAsync("127.0.0.1", PortOf(server));
        await peer.SendExpectingNoReplyAsync(new SecsMessage(1, 1, replyExpected: true)
        {
            Name = "ARE-YOU-THERE",
            SecsItem = Item.A("ping")
        });

        var record = await TakeAsync(records);
        Assert.AreEqual("S1F1W", record.Signature);
        Assert.IsTrue(record.Header.ReplyExpected);
        await WaitFor("未自动回复", () => record.Status == RecordStatus.AutoReplySkipped);
        Assert.AreEqual("未自动回复", record.StatusText);
        Assert.IsEmpty(server.Transcript.Records.Where(entry => entry.Direction == SecsDirection.Out));
    }

    /// <summary>阶段 4 主路径：库里给 S1F1 配了 AutoReply，收到 S1F1W 就回 S1F2，SystemBytes 保持不变。</summary>
    [TestMethod]
    public async Task ConfiguredAutoReplyIsSentAndRecorded()
    {
        await using var server = new HsmsLink { LibraryProvider = SampleLibrary };
        var records = Watch(server);
        server.Connect(Passive(ReservePort()));

        await using var peer = await SecsPeer.ConnectAsync("127.0.0.1", PortOf(server));
        using var request = new SecsMessage(1, 1, replyExpected: true)
        {
            SecsItem = Item.A("ping")
        };
        using var reply = await peer.SendAsync(request);

        Assert.AreEqual((byte)2, reply.F);
        Assert.IsFalse(reply.ReplyExpected);
        StringAssert.Contains(SmlConverter.ToSml(reply), "QODERSECS");

        var inbound = await TakeAsync(records);
        var outbound = await TakeAsync(records);

        Assert.AreEqual("S1F1W", inbound.Signature);
        Assert.AreEqual(SecsDirection.Out, outbound.Direction);
        Assert.AreEqual("S1F2 S1F2_OnlineData", outbound.MessageText);
        Assert.AreEqual(RecordStatus.Sent, outbound.Status);
        Assert.AreEqual(inbound.SystemBytes, outbound.SystemBytes);
        StringAssert.Contains(outbound.Sml, "QODERSECS");
        Assert.IsFalse(outbound.Sml.Contains("S1F2_OnlineData", StringComparison.Ordinal));

        await WaitFor("已自动回复", () => inbound.Status == RecordStatus.Answered);
        Assert.AreEqual("已自动回复", inbound.StatusText);
    }

    /// <summary>库里没有这条 SxFy 时同样不回，记录落到未自动回复。</summary>
    [TestMethod]
    public async Task PrimaryWithoutConfiguredAutoReplyIsMarkedSkipped()
    {
        await using var server = new HsmsLink { LibraryProvider = SampleLibrary };
        var records = Watch(server);
        server.Connect(Passive(ReservePort()));

        await using var peer = await SecsPeer.ConnectAsync("127.0.0.1", PortOf(server));
        await peer.SendExpectingNoReplyAsync(new SecsMessage(7, 25, replyExpected: true)
        {
            SecsItem = Item.A("TEST-RECIPE")
        });

        var inbound = await TakeAsync(records);
        Assert.AreEqual("S7F25W", inbound.Signature);
        await WaitFor("未自动回复", () => inbound.Status == RecordStatus.AutoReplySkipped);
        Assert.IsEmpty(server.Transcript.Records.Where(entry => entry.Direction == SecsDirection.Out));
    }

    /// <summary>
    /// 手工验收走的就是这两条。S1F1 和 S1F13 都没有正文，要确认空正文既能发出去，
    /// 也能按样本库里的配置回出带正文的 Secondary。
    /// </summary>
    [TestMethod]
    public async Task CommonCommandLibraryRepliesToAreYouThereAndEstablishComms()
    {
        await using var server = new HsmsLink { LibraryProvider = CommonCommands };
        server.Connect(Passive(ReservePort()));

        await using var peer = await SecsPeer.ConnectAsync("127.0.0.1", PortOf(server));

        using var areYouThere = new SecsMessage(1, 1, replyExpected: true);
        using var onlineData = await peer.SendAsync(areYouThere);
        Assert.IsFalse(onlineData.ReplyExpected);
        var onlineSml = SmlConverter.ToSml(onlineData);
        StringAssert.Contains(onlineSml, "'S1F2'");
        // 样例库只留结构、不填数据，正文里两个 A 是空的（变量名是 SXML 层的东西，上线不带走），
        // 所以这里认的是根列表的项数——证明带正文的 Secondary 真的回出来了。
        StringAssert.Contains(onlineSml, "<L [2]");

        using var establish = new SecsMessage(1, 13, replyExpected: true);
        using var ack = await peer.SendAsync(establish);
        Assert.IsFalse(ack.ReplyExpected);
        var ackSml = SmlConverter.ToSml(ack);
        StringAssert.Contains(ackSml, "'S1F14'");
        StringAssert.Contains(ackSml, "<L [2]");

        // 对端收到的头里没有名称，本端发出去的那条才带库里的名字。
        var inbound = server.Transcript.Records.Where(record => record.Direction == SecsDirection.In).ToList();
        Assert.AreEqual("S1F1W", inbound[0].MessageText);
        Assert.AreEqual("S1F13W", inbound[1].MessageText);
        await WaitFor("两条都自动回复", () => inbound.All(record => record.Status == RecordStatus.Answered));

        var outbound = server.Transcript.Records.Where(record => record.Direction == SecsDirection.Out).ToList();
        CollectionAssert.AreEqual(
            new[] { "S1F2 S1F2_OnlineData", "S1F14 S1F14_EstablishCommsAck" },
            outbound.Select(record => record.MessageText).ToList());
        Assert.AreEqual(inbound[0].SystemBytes, outbound[0].SystemBytes);
        Assert.AreEqual(inbound[1].SystemBytes, outbound[1].SystemBytes);
    }

    /// <summary>
    /// 出站回调对所有消息都会触发，自动回复不能把在途 W 请求的 SystemBytes 顶掉，
    /// 否则 T3 到点标错记录。
    /// </summary>
    [TestMethod]
    public async Task AutoReplyDoesNotStealTheTimeoutOfAnInFlightRequest()
    {
        await using var server = new HsmsLink { LibraryProvider = SampleLibrary };
        server.Connect(Passive(ReservePort()) with { T3Seconds = 2 });

        await using var peer = await SecsPeer.ConnectAsync("127.0.0.1", PortOf(server));
        await WaitFor("本端就绪", () => server.Phase == LinkPhase.Online);

        // 先发一条对端永远不回的请求，让它在 T3 窗口里挂着；这期间对端发来 S1F1W 触发自动回复。
        using var hanging = new SecsMessage(2, 41, replyExpected: true)
        {
            SecsItem = Item.U4(1)
        };
        var inFlight = server.SendAsync(hanging);
        await WaitFor("请求已发出", () => server.Transcript.Records.Any(entry => entry.Signature == "S2F41W"));

        using var probe = new SecsMessage(1, 1, replyExpected: true)
        {
            SecsItem = Item.A("ping")
        };
        using var autoReply = await peer.SendAsync(probe);
        Assert.AreEqual((byte)2, autoReply.F);

        var outcome = await inFlight;
        StringAssert.Contains(outcome, "超时");

        var timedOut = server.Transcript.Records.Single(entry => entry.Signature == "S2F41W");
        Assert.AreEqual(RecordStatus.TimedOut, timedOut.Status);
        Assert.AreEqual("T3 超时", timedOut.StatusText);

        var sent = server.Transcript.Records.Single(entry => entry.Signature == "S1F2");
        Assert.AreEqual(RecordStatus.Sent, sent.Status);
        Assert.AreNotEqual(sent.SystemBytes, timedOut.SystemBytes);

        var answered = server.Transcript.Records.Single(entry => entry.Signature == "S1F1W");
        await WaitFor("已自动回复", () => answered.Status == RecordStatus.Answered);
    }

    [TestMethod]
    public async Task UnsolicitedSecondaryStillShowsUpAsOrphan()
    {
        await using var server = new HsmsLink();
        var records = Watch(server);
        server.Connect(Passive(ReservePort()));

        await using var peer = await SecsPeer.ConnectAsync("127.0.0.1", PortOf(server));

        // 没有任何请求在等这条 S1F2：Secs4Net 的接收管线不会交出它，只有收发回调能看到。
        await peer.SendAsync(new SecsMessage(1, 2, replyExpected: false)
        {
            Name = "UNWANTED",
            SecsItem = Item.A("nobody")
        });

        var record = await TakeAsync(records);
        Assert.AreEqual("S1F2", record.Signature);
        Assert.AreEqual(RecordStatus.OrphanSecondary, record.Status);
        Assert.AreEqual("无人等待的回复", record.StatusText);
    }

    [TestMethod]
    public async Task SentNonWPrimaryAppearsOnBothEnds()
    {
        var port = ReservePort();
        await using var server = new HsmsLink();
        await using var client = new HsmsLink();
        var incoming = Watch(server);

        server.Connect(Passive(port));
        client.Connect(Active(port));
        await WaitFor("两端就绪", () => server.Phase == LinkPhase.Online && client.Phase == LinkPhase.Online);

        using var request = new SecsMessage(2, 41, replyExpected: false)
        {
            Name = "远程状态",
            SecsItem = Item.U4(1)
        };
        var outcome = await client.SendAsync(request);

        StringAssert.Contains(outcome, "已发出 S2F41");
        var outbound = client.Transcript.Records.Single();
        Assert.AreEqual(SecsDirection.Out, outbound.Direction);
        Assert.AreEqual("已发送", outbound.StatusText);
        StringAssert.Contains(outbound.Sml, "<U4 [1] 1>");

        // 名称要出现在记录里，但不能混进 SML，否则这段 SML 粘不回来。
        Assert.AreEqual("S2F41 远程状态", outbound.MessageText);
        Assert.IsFalse(outbound.Sml.Contains("远程状态", StringComparison.Ordinal));

        var inbound = await TakeAsync(incoming);
        Assert.AreEqual("S2F41", inbound.Signature);
        Assert.AreEqual("已收到", inbound.StatusText);
    }

    /// <summary>测试里的对端不回复，所以带 W 的发送只能走到 T3 超时这条路径。</summary>
    [TestMethod]
    public async Task WPrimaryWithoutReplyIsMarkedT3TimedOut()
    {
        var port = ReservePort();
        await using var server = new HsmsLink();
        await using var client = new HsmsLink();
        var incoming = Watch(server);

        server.Connect(Passive(port));
        client.Connect(Active(port));
        await WaitFor("两端就绪", () => server.Phase == LinkPhase.Online && client.Phase == LinkPhase.Online);

        using var request = new SecsMessage(1, 1, replyExpected: true)
        {
            SecsItem = Item.A("ping")
        };
        var outcome = await client.SendAsync(request);

        StringAssert.Contains(outcome, "超时");
        StringAssert.Contains(outcome, "T3");
        var outbound = client.Transcript.Records.Single();
        Assert.AreEqual(RecordStatus.TimedOut, outbound.Status);
        Assert.IsGreaterThanOrEqualTo(800, outbound.ElapsedMilliseconds ?? 0);

        var inbound = await TakeAsync(incoming);
        Assert.AreEqual("S1F1W", inbound.Signature);
        await WaitFor("未自动回复", () => inbound.Status == RecordStatus.AutoReplySkipped);
    }

    /// <summary>阶段 3 的主路径：本端发 W 请求，对端回复，记录要按 SystemBytes 配对并算出耗时。</summary>
    [TestMethod]
    public async Task WPrimaryWithReplyIsPairedWithElapsed()
    {
        var port = ReservePort();
        await using var server = new HsmsLink();
        server.Connect(Passive(port) with { T3Seconds = 10 });

        await using var peer = await SecsPeer.ConnectAsync("127.0.0.1", PortOf(server));
        peer.StartReplying();
        await WaitFor("本端就绪", () => server.Phase == LinkPhase.Online);

        using var request = new SecsMessage(1, 1, replyExpected: true)
        {
            Name = "在线确认",
            SecsItem = Item.A("ping")
        };
        var outcome = await server.SendAsync(request);

        StringAssert.Contains(outcome, "已发出 S1F1W");
        StringAssert.Contains(outcome, "收到 S1F2");

        var outbound = server.Transcript.Records.Single(record => record.Direction == SecsDirection.Out);
        var inbound = server.Transcript.Records.Single(record => record.Direction == SecsDirection.In);

        Assert.AreEqual(RecordStatus.SecondaryReceived, outbound.Status);
        Assert.IsNotNull(outbound.ElapsedMilliseconds);
        Assert.AreEqual("S1F1W 在线确认", outbound.MessageText);
        Assert.AreEqual("S1F2", inbound.Signature);
        Assert.AreEqual(RecordStatus.PairedSecondary, inbound.Status);
        Assert.AreEqual(outbound.SystemBytes, inbound.SystemBytes);
        Assert.AreEqual(outbound.SystemBytes, inbound.LinkedSystemBytes);
    }

    /// <summary>
    /// 规格 §8：1MB 的 B 数据双向收发。正文不许留在记录里，只许留在 sml.log，
    /// 而且读回来要和一次性生成的 SML 一模一样。
    /// </summary>
    [TestMethod]
    public async Task OneMegabyteBinaryTravelsBothDirectionsAndIsLoggedWhole()
    {
        var directory = NewSessionDirectory();
        try
        {
            await using var server = new HsmsLink { SessionDirectoryFactory = _ => directory };
            var records = Watch(server);
            server.Connect(LargeTransfer(Passive(ReservePort())));

            await using var peer = await SecsPeer.ConnectAsync("127.0.0.1", PortOf(server));
            peer.StartCollecting();
            await WaitFor("本端就绪", () => server.Phase == LinkPhase.Online);

            var payload = Pattern(1024 * 1024);

            using (var message = new SecsMessage(6, 11, replyExpected: false) { SecsItem = Item.B(payload) })
            {
                await peer.SendAsync(message);
            }

            var inbound = await TakeAsync(records, 120_000);
            Assert.AreEqual("S6F11", inbound.Signature);
            Assert.AreEqual(SecsDirection.In, inbound.Direction);
            AssertIsFolded(inbound);
            Assert.AreEqual(HashText(SmlOf(payload)), HashText(await ReadBlockAsync(server.Log!, inbound)));

            using (var message = new SecsMessage(6, 11, replyExpected: false) { Name = "大数据", SecsItem = Item.B(payload) })
            {
                StringAssert.Contains(await server.SendAsync(message), "已发出 S6F11");
            }

            var outbound = await TakeAsync(records, 120_000);
            Assert.AreEqual(SecsDirection.Out, outbound.Direction);
            AssertIsFolded(outbound);
            // 名称不进 SML：发出去的正文和收进来的那份完全相同。
            Assert.AreEqual(HashText(SmlOf(payload)), HashText(await ReadBlockAsync(server.Log!, outbound)));

            var received = TakeReceived(peer);
            Assert.AreEqual(payload.Length, received.Length);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(payload)), received.Hash);

            // 大正文不能把连接带崩：收发线程上真抛了异常，这里早就掉回 Retrying 了。
            Assert.AreEqual(LinkPhase.Online, server.Phase);
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    /// <summary>
    /// 规格 §8：10MB 的 B 数据双向收发。这一档只验证搬得动、正文没留在内存里、
    /// 两个方向的日志正文一致；文本与 SML 逐字对齐由 1MB 那条测试保证。
    /// </summary>
    [TestMethod]
    public async Task TenMegabyteBinaryTravelsBothDirections()
    {
        var directory = NewSessionDirectory();
        try
        {
            await using var server = new HsmsLink { SessionDirectoryFactory = _ => directory };
            var records = Watch(server);
            server.Connect(LargeTransfer(Passive(ReservePort())));

            await using var peer = await SecsPeer.ConnectAsync("127.0.0.1", PortOf(server));
            peer.StartCollecting();
            await WaitFor("本端就绪", () => server.Phase == LinkPhase.Online);

            var payload = Pattern(10 * 1024 * 1024);

            using (var message = new SecsMessage(6, 11, replyExpected: false) { SecsItem = Item.B(payload) })
            {
                await peer.SendAsync(message);
            }

            var inbound = await TakeAsync(records, 300_000);
            AssertIsFolded(inbound);

            using (var message = new SecsMessage(6, 11, replyExpected: false) { SecsItem = Item.B(payload) })
            {
                StringAssert.Contains(await server.SendAsync(message), "已发出 S6F11");
            }

            var outbound = await TakeAsync(records, 300_000);
            AssertIsFolded(outbound);

            var log = server.Log!;
            Assert.AreEqual(await HashBlockAsync(log, inbound), await HashBlockAsync(log, outbound));
            Assert.IsGreaterThan(payload.Length * 3L, BlockLength(log, inbound));

            var received = TakeReceived(peer);
            Assert.AreEqual(payload.Length, received.Length);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(payload)), received.Hash);

            // 大正文不能把连接带崩：收发线程上真抛了异常，这里早就掉回 Retrying 了。
            Assert.AreEqual(LinkPhase.Online, server.Phase);
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    [TestMethod]
    public async Task SendingBeforeConnectIsRejected()
    {
        await using var link = new HsmsLink();
        using var request = new SecsMessage(2, 41, replyExpected: false)
        {
            SecsItem = Item.U4(1)
        };

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => link.SendAsync(request));

        StringAssert.Contains(error.Message, "尚未连接");
        Assert.IsEmpty(link.Transcript.Records);
    }

    [TestMethod]
    public async Task SecondPassiveLinkOnTheSamePortReportsListenFailure()
    {
        var port = ReservePort();
        await using var first = new HsmsLink();
        first.Connect(Passive(port));
        await WaitFor("第一个监听", () => first.Phase is LinkPhase.Connecting or LinkPhase.Connected or LinkPhase.Online);

        await using var second = new HsmsLink();
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Task.Run(() => second.Connect(Passive(port))));

        StringAssert.Contains(error.Message, "监听");
        Assert.AreEqual(LinkPhase.Offline, second.Phase);
    }

    [TestMethod]
    public async Task ConnectingWhileAlreadyConnectedIsRejected()
    {
        var port = ReservePort();
        await using var link = new HsmsLink();
        link.Connect(Passive(port));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Task.Run(() => link.Connect(Passive(port))));

        StringAssert.Contains(error.Message, "已经有一条连接");
    }

    /// <summary>
    /// 重连要从空记录开始：界面在连接前会清空列表，关联器要是还挂着上一次会话的记录，
    /// 那些行内 SML（每条最多 16KB）就一直回不到内存里。顺带验证断开时端口真的放掉了。
    /// </summary>
    [TestMethod]
    public async Task ReconnectingStartsFromAnEmptyTranscript()
    {
        var port = ReservePort();
        await using var server = new HsmsLink();
        server.Connect(Passive(port));

        await using var client = new HsmsLink();
        client.Connect(Active(port));
        await WaitFor("两端就绪", () => server.Phase == LinkPhase.Online && client.Phase == LinkPhase.Online);

        using (var request = new SecsMessage(2, 41, replyExpected: false) { SecsItem = Item.U4(1) })
        {
            await client.SendAsync(request);
        }

        await WaitFor("第一次会话留下记录", () => server.Transcript.Records.Count > 0);

        await client.DisconnectAsync();
        await server.DisconnectAsync();
        Assert.AreEqual(LinkPhase.Offline, server.Phase);

        server.Connect(Passive(port));
        Assert.IsEmpty(server.Transcript.Records);
    }

    /// <summary>
    /// 主动断开是自己 Cancel 接收循环，Secs4Net 把这当成错误报上来（Passive 侧一次能报两回）。
    /// 这种自找的取消不许进 detail.log，否则每次断开都留两行让人以为出了故障；
    /// 会话收尾的「会话结束」必须还在，证明日志是正常关掉的。
    /// </summary>
    [TestMethod]
    public async Task DisconnectDoesNotLogSelfInflictedCancellation()
    {
        var directory = NewSessionDirectory();
        try
        {
            var port = ReservePort();
            await using var server = new HsmsLink { SessionDirectoryFactory = _ => directory };
            server.Connect(Passive(port));

            await using var client = new HsmsLink();
            client.Connect(Active(port));
            await WaitFor("两端就绪", () => server.Phase == LinkPhase.Online && client.Phase == LinkPhase.Online);

            await server.DisconnectAsync();
            await client.DisconnectAsync();

            var detail = await ReadDetailLogAsync(directory);
            StringAssert.Contains(detail, "会话结束");
            Assert.DoesNotContain(detail, "The operation was canceled.", "断开不该留下取消噪音");
            Assert.DoesNotContain(detail, "断开时", "干净断开不该有任何「断开时」报错");
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    /// <summary>
    /// 对方先断开后，Active 侧的 Secs4Net 仍按 T5 在后台等对端回来，重连被拒是正常过程，
    /// 但日志里不许写成没来由的「异常」：这些行要归到「对方断开后」，和界面对上。
    /// 还挂在「等待回复」上的记录，在本端点断开时落到「连接已断开」。
    /// </summary>
    [TestMethod]
    public async Task PeerLostLabelsBackgroundRetryErrorsAndFinalizesPendingRecords()
    {
        var directory = NewSessionDirectory();
        try
        {
            var port = ReservePort();
            await using var server = new HsmsLink();
            server.Connect(Passive(port));

            await using var client = new HsmsLink { SessionDirectoryFactory = _ => directory };
            client.Connect(Active(port) with { T3Seconds = 30 });
            await WaitFor("两端就绪", () => server.Phase == LinkPhase.Online && client.Phase == LinkPhase.Online);

            // 对端没有消息库，不会回复；T3 放到 30 秒，让这条记录一直挂在「等待回复」上。
            using var request = new SecsMessage(1, 1, replyExpected: true);
            var sendTask = client.SendAsync(request);
            await WaitFor("请求挂起", () => client.Transcript.Records.Count > 0);

            await server.DisconnectAsync();
            await WaitFor("判定对方断开", () => client.Phase == LinkPhase.PeerLost);

            // T5 是 1 秒：等两个多周期，后台重连被拒的行必须归到「对方断开后」。
            await Task.Delay(2500);
            var beforeClose = await ReadDetailLogAsync(directory);
            StringAssert.Contains(beforeClose, "连接状态 PeerLost");
            StringAssert.Contains(beforeClose, "对方断开后");
            Assert.DoesNotContain(beforeClose, "异常", "对方断开后的报错不许写成没来由的「异常」");
            Assert.AreEqual(LinkPhase.PeerLost, client.Phase);

            await client.DisconnectAsync();
            await sendTask.ContinueWith(_ => { }, TaskScheduler.Default);

            var detail = await ReadDetailLogAsync(directory);
            StringAssert.Contains(detail, "会话结束");
            Assert.HasCount(1, client.Transcript.Records);
            Assert.AreEqual(RecordStatus.LinkClosed, client.Transcript.Records[0].Status);
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    [TestMethod]
    public void OptionsMappingUsesProfileTimersAndMode()
    {
        var options = SecsGemOptionsFactory.Create(new ConnectionProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Role = EndpointRole.Equipment,
            Mode = ConnectionMode.Passive,
            IpAddress = "127.0.0.1",
            Port = 5060,
            DeviceId = 7,
            T3Seconds = 12,
            T5Seconds = 10,
            T6Seconds = 5,
            T7Seconds = 10,
            T8Seconds = 5,
            LinkTestEnabled = false,
            LinkTestIntervalSeconds = 30
        });

        Assert.AreEqual(12_000, options.T3);
        Assert.AreEqual(10_000, options.T5);
        Assert.AreEqual(5_000, options.T6);
        Assert.IsFalse(options.IsActive);
        Assert.AreEqual(5060, options.Port);
        Assert.AreEqual((ushort)7, options.DeviceId);
        Assert.AreEqual(0, options.LinkTestInterval);
    }

    [TestMethod]
    public void OptionsMappingRejectsInvalidProfile()
    {
        var profile = ConnectionProfile.CreateDefault() with { Port = 0 };

        var error = Assert.ThrowsExactly<ArgumentException>(() => SecsGemOptionsFactory.Create(profile));

        StringAssert.Contains(error.Message, "端口");
    }

    private static int PortOf(HsmsLink link) =>
        link.Profile is null ? throw new InvalidOperationException("尚未连接") : link.Profile.Port;

    /// <summary>样例库里 S1F1 配了 AutoReply 指向 S1F2_OnlineData，其余消息都没配。</summary>
    private static MessageLibrary SampleLibrary() => MessageLibrary.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "sample-library.sxml")));

    /// <summary>随程序发的常用命令库，S1F1、S1F13 这些都配好了自动回复。</summary>
    private static MessageLibrary CommonCommands() => MessageLibrary.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "common-commands.sxml")));

    private static BlockingCollection<CommunicationRecord> Watch(HsmsLink link)
    {
        var sink = new BlockingCollection<CommunicationRecord>();
        link.Transcript.RecordAdded += record => sink.Add(record);
        return sink;
    }

    private static Task<CommunicationRecord> TakeAsync(BlockingCollection<CommunicationRecord> records) =>
        TakeAsync(records, 5000);

    private static Task<CommunicationRecord> TakeAsync(BlockingCollection<CommunicationRecord> records, int timeout)
    {
        if (records.TryTake(out var record, timeout))
        {
            return Task.FromResult(record);
        }

        throw new AssertFailedException($"{timeout / 1000} 秒内没有收到通信记录。");
    }

    /// <summary>大消息跑起来慢，定时器的余量也要跟着放大，不然测的是超时不是收发。</summary>
    private static ConnectionProfile LargeTransfer(ConnectionProfile profile) => profile with
    {
        T3Seconds = 60,
        T5Seconds = 10,
        T6Seconds = 30,
        T7Seconds = 10,
        T8Seconds = 30
    };

    private static string NewSessionDirectory() =>
        Path.Combine(Path.GetTempPath(), "qodersecs-loopback-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 会话还没结束时日志仍被写端占着，普通 ReadAllText 会因共享冲突失败；
    /// 这里显式允许读写共享，中途也能读。
    /// </summary>
    private static async Task<string> ReadDetailLogAsync(string directory)
    {
        using var stream = new FileStream(
            Path.Combine(directory, "detail.log"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static void DeleteQuietly(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 7) + 3);
        }

        return bytes;
    }

    private static string SmlOf(byte[] payload)
    {
        using var message = new SecsMessage(6, 11, replyExpected: false) { SecsItem = Item.B(payload) };
        return SmlConverter.ToSml(message);
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.TrimEnd())));

    /// <summary>大消息的记录只该带元数据：正文在 sml.log 里，展开时才读。</summary>
    private static void AssertIsFolded(CommunicationRecord record)
    {
        Assert.IsNull(record.Sml, "大消息的正文不该留在记录里。");
        Assert.IsNotNull(record.SmlSequence);
        Assert.IsGreaterThan((long)TranscriptLog.InlineLimit, record.SmlByteLength ?? 0);
        StringAssert.Contains(record.DetailText, "sml.log");
    }

    private static long BlockLength(TranscriptLog log, CommunicationRecord record)
    {
        Assert.IsTrue(log.TryGetSmlLocation(record.SmlSequence!.Value, out var location));
        return location.Length;
    }

    private static async Task<string> ReadBlockAsync(TranscriptLog log, CommunicationRecord record)
    {
        Assert.IsTrue(log.TryGetSmlLocation(record.SmlSequence!.Value, out var location));
        var text = new StringBuilder();
        await using var reader = await log.OpenBlockReaderAsync(location);
        string? page;
        while ((page = await reader.ReadPageAsync()) is not null)
        {
            text.Append(page);
        }

        return text.ToString();
    }

    /// <summary>逐页算哈希，不把整段正文读进内存——10MB 的 B 展开成 SML 有三十兆。</summary>
    private static async Task<string> HashBlockAsync(TranscriptLog log, CommunicationRecord record)
    {
        Assert.IsTrue(log.TryGetSmlLocation(record.SmlSequence!.Value, out var location));
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var reader = await log.OpenBlockReaderAsync(location))
        {
            string? page;
            while ((page = await reader.ReadPageAsync()) is not null)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(page));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static (int Length, string Hash) TakeReceived(SecsPeer peer)
    {
        if (peer.Received.TryTake(out var received, 300_000))
        {
            return received;
        }

        throw new AssertFailedException("5 分钟内对端没有收到消息。");
    }

    private static async Task WaitFor(string what, Func<bool> condition)    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        if (!condition())
        {
            throw new AssertFailedException($"等待“{what}”超时。");
        }
    }

    private static ConnectionProfile Passive(int port) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Role = EndpointRole.Equipment,
        Mode = ConnectionMode.Passive,
        IpAddress = "127.0.0.1",
        Port = port,
        DeviceId = 0,
        T3Seconds = 1,
        T5Seconds = 1,
        T6Seconds = 1,
        T7Seconds = 1,
        T8Seconds = 1,
        LinkTestEnabled = false,
        LinkTestIntervalSeconds = 1
    };

    private static ConnectionProfile Active(int port) => Passive(port) with
    {
        Role = EndpointRole.Host,
        Mode = ConnectionMode.Active
    };

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>用 Secs4Net 原语模拟对端机台：能主动发，也能按需回复收到的 Primary。</summary>
    private sealed class SecsPeer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly HsmsConnection _connection;
        private readonly SecsGem _gem;

        public BlockingCollection<(int Length, string Hash)> Received { get; } = new();

        private SecsPeer(HsmsConnection connection, SecsGem gem)
        {
            _connection = connection;
            _gem = gem;
        }

        public static async Task<SecsPeer> ConnectAsync(string ip, int port)
        {
            var holder = Options.Create(new SecsGemOptions
            {
                IpAddress = ip,
                Port = port,
                DeviceId = 0,
                IsActive = true,
                T3 = 1000,
                T5 = 1000,
                T6 = 1000,
                T7 = 1000,
                T8 = 1000,
                LinkTestInterval = 0
            });
            var logger = new NullLogger();
            var connection = new HsmsConnection(holder, logger);
            var gem = new SecsGem(holder, connection, logger);
            var peer = new SecsPeer(connection, gem);
            connection.Start(peer._cts.Token);

            for (var i = 0; i < 100 && connection.State != ConnectionState.Selected; i++)
            {
                await Task.Delay(50);
            }

            if (connection.State != ConnectionState.Selected)
            {
                await peer.DisposeAsync();
                throw new AssertFailedException($"对端无法选中 {ip}:{port}。");
            }

            return peer;
        }

        public Task<SecsMessage> SendAsync(SecsMessage message) => _gem.SendAsync(message, _cts.Token);

        public async Task SendExpectingNoReplyAsync(SecsMessage message)
        {
            try
            {
                using var ignored = await SendAsync(message);
            }
            catch (SecsException)
            {
                // 本阶段不回复 W 请求，对端 T3 超时是预期结果。
            }
        }

        /// <summary>把收到的每条 Primary 的正文大小和哈希收下来，用来验证大消息真的完整到了对端。</summary>
        public void StartCollecting() =>
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var request in _gem.GetPrimaryMessageAsync(_cts.Token))
                    {
                        using var primary = request.PrimaryMessage;
                        var bytes = primary.SecsItem!.GetMemory<byte>();
                        Received.Add((bytes.Length, Convert.ToHexString(SHA256.HashData(bytes.Span))));
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
                {
                    // 对端释放时管线结束属于正常路径。
                }
            });

        /// <summary>把收到的每条 Primary 都回一条 Secondary，用来验证本端发出的 W 请求。</summary>
        public void StartReplying() =>
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var request in _gem.GetPrimaryMessageAsync(_cts.Token))
                    {
                        var primary = request.PrimaryMessage;
                        using var reply = new SecsMessage(primary.S, (byte)(primary.F + 1), replyExpected: false)
                        {
                            SecsItem = Item.A("pong")
                        };
                        await request.TryReplyAsync(reply, _cts.Token);
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
                {
                    // 对端释放时管线结束属于正常路径。
                }
            });

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _gem.Dispose();
            await _connection.DisposeAsync();
            _cts.Dispose();
        }

        private sealed class NullLogger : ISecsGemLogger
        {
            public void Debug(string msg)
            {
            }

            public void Info(string msg)
            {
            }

            public void Warning(string msg)
            {
            }

            public void Error(string msg)
            {
            }

            public void Error(string msg, Exception ex)
            {
            }

            public void Error(string msg, SecsMessage? message, Exception? ex)
            {
            }

            public void MessageIn(SecsMessage msg, int id)
            {
            }

            public void MessageOut(SecsMessage msg, int id)
            {
            }
        }
    }
}

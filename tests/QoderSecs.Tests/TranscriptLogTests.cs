using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QoderSecs.Core.Connection;
using QoderSecs.Core.Messages;
using Secs4Net;

namespace QoderSecs.Tests;

[TestClass]
public sealed class TranscriptLogTests
{
    [TestMethod]
    public async Task InlineSmlIsWrittenUnderAnIndexLineAndReadBackIdentical()
    {
        var directory = NewSessionDirectory();
        try
        {
            string smlLogPath;
            long sequence;
            await using (var log = TranscriptLog.Open(directory))
            {
                smlLogPath = log.SmlLogPath;
                sequence = log.EnqueueInlineSml(
                    SecsDirection.Out,
                    0x09,
                    new SecsHeader(1, 1, true, "在线确认"),
                    "<S1F1W />");

                var location = await WaitForLocationAsync(log, sequence);
                Assert.AreEqual("<S1F1W />" + Environment.NewLine, await ReadBlockAsync(log, location));
            }

            var indexLine = (await File.ReadAllLinesAsync(smlLogPath))[0];
            StringAssert.Contains(indexLine, $"# seq={sequence} sys=00000009 dir=Out S1F1W 在线确认");
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
    public async Task DetailLinesAreTimestampedAndKeptInOrder()
    {
        var directory = NewSessionDirectory();
        try
        {
            string detailLogPath;
            await using (var log = TranscriptLog.Open(directory))
            {
                detailLogPath = log.DetailLogPath;
                log.EnqueueDetail("连接状态 Online");
                log.EnqueueDetail("自动回复 收到 S1F1W sys=00000009：按库里 S1F1 的 AutoReply 配置发出「S1F2」（同 sys）");
            }

            var lines = await File.ReadAllLinesAsync(detailLogPath);
            Assert.HasCount(2, lines);
            StringAssert.Matches(lines[0], new Regex(@"^\d\d:\d\d:\d\d\.\d\d\d "));
            StringAssert.EndsWith(lines[0], " 连接状态 Online");
            StringAssert.EndsWith(lines[1], "（同 sys）");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    /// <summary>
    /// 大消息不落内存：在调用线程上边格式化边写盘，读回来要和一次性生成的 SML 完全一致。
    /// </summary>
    [TestMethod]
    public async Task LargeMessageIsStreamedToTheLogWithoutBuildingTheWholeText()
    {
        var directory = NewSessionDirectory();
        var payload = Pattern(64 * 1024);
        try
        {
            using var message = new SecsMessage(6, 12, replyExpected: false)
            {
                SecsItem = Item.B(payload)
            };
            Assert.IsGreaterThan((long)TranscriptLog.InlineLimit, SmlConverter.EstimateTextBytes(message));

            await using var log = TranscriptLog.Open(directory);
            var written = log.WriteSmlBlocking(SecsDirection.In, 0x0B, new SecsHeader(6, 12, false), message);

            Assert.IsNotNull(written);
            Assert.IsTrue(log.TryGetSmlLocation(written.Value.Sequence, out var location));
            Assert.IsGreaterThan((long)payload.Length, location.Length);
            Assert.AreEqual(location.Length, written.Value.ByteLength, "记录上显示的大小应当是落盘后的实际长度");

            var readBack = await ReadBlockAsync(log, location);
            Assert.AreEqual(Hash(SmlConverter.ToSml(message)), Hash(readBack));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    /// <summary>分段读取必须给整行，读完要返回 null，界面那边才不会转不出来。</summary>
    [TestMethod]
    public async Task PagedReadHandsBackWholeLinesAndStopsAtTheEndOfTheBlock()
    {
        var directory = NewSessionDirectory();
        try
        {
            // 比读取缓冲区（256KB）大，逼出多页。
            var body = string.Concat(
                Enumerable.Range(0, 12000).Select(i => $"第 {i} 行 <A '值{i}'>{Environment.NewLine}"));

            await using var log = TranscriptLog.Open(directory);
            var sequence = log.EnqueueInlineSml(SecsDirection.In, 0x0C, new SecsHeader(6, 11, false), body);
            var location = await WaitForLocationAsync(log, sequence);

            var pages = new List<string>();
            await using (var reader = await log.OpenBlockReaderAsync(location))
            {
                string? page;
                while ((page = await reader.ReadPageAsync()) is not null)
                {
                    pages.Add(page);
                }
            }

            Assert.IsGreaterThan(1, pages.Count);
            Assert.AreEqual(body + Environment.NewLine, string.Concat(pages));
            foreach (var page in pages.SkipLast(1))
            {
                StringAssert.EndsWith(page, "\n");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    /// <summary>
    /// 记录里写的长度比文件里实际有的多（日志还在缓冲里没落盘，或者文件被截断过）时，
    /// 分段读取必须在文件尾停下来。当成"还没读完"就会一直返回空页，界面那个循环永远出不来，CPU 跑满。
    /// </summary>
    [TestMethod]
    public async Task PagedReadStopsWhenTheFileIsShorterThanTheRecordedLength()
    {
        var directory = NewSessionDirectory();
        try
        {
            await using var log = TranscriptLog.Open(directory);
            var sequence = log.EnqueueInlineSml(SecsDirection.In, 0x0D, new SecsHeader(6, 11, false), "短正文");
            var location = await WaitForLocationAsync(log, sequence);

            var overrun = location with { Length = location.Length + (64 * 1024) };

            // 读的时候挂一个到点就取消的令牌：万一又退化成一直返回空页，
            // 这里是失败退出，而不是把整个测试进程吊死。
            using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var text = new StringBuilder();
            await using (var reader = await log.OpenBlockReaderAsync(overrun, giveUp.Token))
            {
                string? page;
                while ((page = await reader.ReadPageAsync(giveUp.Token)) is not null)
                {
                    text.Append(page);
                }
            }

            Assert.AreEqual("短正文" + Environment.NewLine, text.ToString());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    /// <summary>ASCII 元素不给原始内存，估算大小不能因此抛异常把连接带崩。</summary>
    [TestMethod]
    public void EstimateTextBytesSurvivesAsciiItems()    {
        using var message = new SecsMessage(1, 1, replyExpected: true)
        {
            SecsItem = Item.L(Item.A(new string('x', 100)), Item.B([1, 2, 3]), Item.U4(7))
        };

        var estimate = SmlConverter.EstimateTextBytes(message);

        Assert.IsGreaterThanOrEqualTo(100L, estimate);
    }

    /// <summary>
    /// 日志写不下去时（磁盘满、会话已经关掉）只能丢正文，不能把异常抛回 Secs4Net 的收发线程——
    /// 那条线程上抛异常会把整条 HSMS 连接带崩，大 PP body 就是这样闪退的。
    /// </summary>
    [TestMethod]
    public async Task WriteSmlBlockingGivesUpQuietlyWhenTheLogCannotTakeIt()
    {
        var directory = NewSessionDirectory();
        try
        {
            var log = TranscriptLog.Open(directory);
            await log.DisposeAsync();

            using var message = new SecsMessage(6, 12, replyExpected: false)
            {
                SecsItem = Item.B(Pattern(64 * 1024))
            };

            var written = log.WriteSmlBlocking(SecsDirection.Out, 0x1A, new SecsHeader(6, 12, false), message);

            Assert.IsNull(written, "写不进去就该返回 null，由调用方降级成没有正文的记录。");
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
        Path.Combine(Path.GetTempPath(), "qodersecs-log-tests", Guid.NewGuid().ToString("N"));

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 7) + 3);
        }

        return bytes;
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.TrimEnd())));

    private static async Task<SmlLocation> WaitForLocationAsync(TranscriptLog log, long sequence)
    {
        for (var i = 0; i < 100; i++)
        {
            if (log.TryGetSmlLocation(sequence, out var location))
            {
                return location;
            }

            await Task.Delay(50);
        }

        throw new AssertFailedException("后台队列 5 秒内没有写完这条正文。");
    }

    private static async Task<string> ReadBlockAsync(TranscriptLog log, SmlLocation location)
    {
        var text = new StringBuilder();
        await using var reader = await log.OpenBlockReaderAsync(location);
        string? page;
        while ((page = await reader.ReadPageAsync()) is not null)
        {
            text.Append(page);
        }

        return text.ToString();
    }
}

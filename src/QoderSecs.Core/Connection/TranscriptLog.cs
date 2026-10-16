using System.Globalization;
using System.Text;
using System.Threading.Channels;
using QoderSecs.Core.Messages;
using Secs4Net;

namespace QoderSecs.Core.Connection;

/// <summary>sml.log 里一段正文的位置。Offset 指向 SML 文本第一个字节，不含前面的索引行。</summary>
public readonly record struct SmlLocation(long Sequence, long Offset, long Length);

/// <summary>
/// 一次连接会话的磁盘日志：sml.log 放完整 SML，detail.log 放连接事件、头信息、耗时、异常和回复来源。
///
/// detail.log 和小消息的 SML 走单消费者后台队列顺序写。大消息的 SML 在调用线程上流式写：
/// 消息本体由 Secs4Net 或调用方持有，一返回就可能被释放，不能交给后台线程慢慢写。
/// 两类写共用一把锁，保证 sml.log 里不会交错。
/// </summary>
public sealed class TranscriptLog : IAsyncDisposable
{
    /// <summary>小于这个大小的 SML 直接留在记录里，详情秒开；更大的只存序号，展开时读盘。</summary>
    public const int InlineLimit = 16 * 1024;

    private readonly FileStream _smlStream;
    private readonly StreamWriter _smlWriter;
    private readonly StreamWriter _detailWriter;
    private readonly Channel<LogJob> _queue = Channel.CreateUnbounded<LogJob>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;
    private readonly SemaphoreSlim _smlGate = new(1, 1);
    private readonly Dictionary<long, SmlLocation> _locations = new();
    private long _nextSequence;

    private TranscriptLog(string smlLogPath, string detailLogPath, FileStream smlStream, StreamWriter detailWriter)
    {
        SmlLogPath = smlLogPath;
        DetailLogPath = detailLogPath;
        _smlStream = smlStream;
        _smlWriter = new StreamWriter(smlStream, new UTF8Encoding(false)) { AutoFlush = false };
        _detailWriter = detailWriter;
        _pump = Task.Run(PumpAsync);
    }

    public string SmlLogPath { get; }

    public string DetailLogPath { get; }

    public static TranscriptLog Open(string sessionDirectory)
    {
        Directory.CreateDirectory(sessionDirectory);
        var smlLogPath = Path.Combine(sessionDirectory, "sml.log");
        var detailLogPath = Path.Combine(sessionDirectory, "detail.log");
        var smlStream = new FileStream(
            smlLogPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var detailStream = new FileStream(
            detailLogPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var detailWriter = new StreamWriter(detailStream, new UTF8Encoding(false)) { AutoFlush = false };
        return new TranscriptLog(smlLogPath, detailLogPath, smlStream, detailWriter);
    }

    /// <summary>排一条小消息的正文。返回的序号写进记录，读盘时靠它找位置。</summary>
    public long EnqueueInlineSml(SecsDirection direction, int systemBytes, SecsHeader header, string sml)
    {
        var sequence = Interlocked.Increment(ref _nextSequence);
        _queue.Writer.TryWrite(LogJob.InlineSml(sequence, direction, systemBytes, header, sml));
        return sequence;
    }

    /// <summary>
    /// 大消息在这里边格式化边落盘，内存里不留正文。调用线程会被占住到写完为止，
    /// 但这条线程是 Secs4Net 的收发线程，不是 UI 线程。
    /// 写不下去时返回 null：这条消息的正文丢掉，通信记录和连接都照常。
    /// </summary>
    public (long Sequence, long ByteLength)? WriteSmlBlocking(
        SecsDirection direction,
        int systemBytes,
        SecsHeader header,
        SecsMessage message)
    {
        var sequence = Interlocked.Increment(ref _nextSequence);
        try
        {
            _smlGate.Wait();
            try
            {
                var location = WriteBlockAsync(sequence, direction, systemBytes, header, message)
                    .GetAwaiter()
                    .GetResult();
                lock (_locations)
                {
                    _locations[sequence] = location;
                }

                return (sequence, location.Length);
            }
            finally
            {
                _smlGate.Release();
            }
        }
        catch (Exception exception)
        {
            // 这个方法是在收发回调里被调用的：异常抛出去会把整条 HSMS 连接带崩，
            // 所以磁盘满、文件被占用、正文里有转不出来的格式，一律只丢正文。
            EnqueueDetail($"SML 正文没能写进日志 seq={sequence} {direction} {header.Signature}：{exception.Message}");
            return null;
        }
    }

    /// <summary>事件行：连接状态、状态变化、回复来源、异常。时间戳在这里统一加。</summary>
    public void EnqueueDetail(string line) =>
        _queue.Writer.TryWrite(
            LogJob.DetailLine($"{DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {line}"));

    public bool TryGetSmlLocation(long sequence, out SmlLocation location)
    {
        lock (_locations)
        {
            return _locations.TryGetValue(sequence, out location);
        }
    }

    public Task<SmlBlockReader> OpenBlockReaderAsync(SmlLocation location, CancellationToken cancellation = default) =>
        SmlBlockReader.OpenAsync(SmlLogPath, location, cancellation);

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (IOException)
        {
            // 队列里的内容写失败不该挡住断开连接，文件关闭时还会再 flush 一次。
        }

        await _smlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _smlWriter.FlushAsync().ConfigureAwait(false);
            await _detailWriter.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _smlGate.Release();
        }

        _smlWriter.Dispose();
        _detailWriter.Dispose();
        _smlGate.Dispose();
    }

    private async Task PumpAsync()
    {
        await foreach (var job in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // 每一条都要兜住 IOException：漏出去 _pump 就死了，
            // 无界队列会一直把日志堆在内存里，界面上看着正常，其实早就没在写盘了。
            try
            {
                if (job.Detail is { } detail)
                {
                    await _detailWriter.WriteLineAsync(detail).ConfigureAwait(false);
                    await _detailWriter.FlushAsync().ConfigureAwait(false);
                    continue;
                }

                await _smlGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var location = await WriteBlockAsync(
                            job.Sequence,
                            job.Direction,
                            job.SystemBytes,
                            job.Header,
                            job.Sml!)
                        .ConfigureAwait(false);
                    lock (_locations)
                    {
                        _locations[job.Sequence] = location;
                    }
                }
                finally
                {
                    _smlGate.Release();
                }
            }
            catch (IOException)
            {
                // 磁盘写不进去时通信不能跟着停，丢掉这一条即可。
            }
        }
    }

    private async Task<SmlLocation> WriteBlockAsync(
        long sequence,
        SecsDirection direction,
        int systemBytes,
        SecsHeader header,
        SecsMessage message)
    {
        await _smlWriter.WriteLineAsync(IndexLine(sequence, direction, systemBytes, header)).ConfigureAwait(false);
        await _smlWriter.FlushAsync().ConfigureAwait(false);
        var offset = _smlStream.Position;

        await SmlConverter.WriteSmlAsync(_smlWriter, message).ConfigureAwait(false);
        await _smlWriter.WriteLineAsync().ConfigureAwait(false);
        await _smlWriter.FlushAsync().ConfigureAwait(false);

        return new SmlLocation(sequence, offset, _smlStream.Position - offset);
    }

    private async Task<SmlLocation> WriteBlockAsync(
        long sequence,
        SecsDirection direction,
        int systemBytes,
        SecsHeader header,
        string sml)
    {
        await _smlWriter.WriteLineAsync(IndexLine(sequence, direction, systemBytes, header)).ConfigureAwait(false);
        await _smlWriter.FlushAsync().ConfigureAwait(false);
        var offset = _smlStream.Position;

        await _smlWriter.WriteAsync(sml).ConfigureAwait(false);
        await _smlWriter.WriteLineAsync().ConfigureAwait(false);
        await _smlWriter.FlushAsync().ConfigureAwait(false);

        return new SmlLocation(sequence, offset, _smlStream.Position - offset);
    }

    private static string IndexLine(long sequence, SecsDirection direction, int systemBytes, SecsHeader header) =>
        $"# seq={sequence} sys={systemBytes:X8} dir={(direction == SecsDirection.Out ? "Out" : "In")} " +
        $"{header.Signature} {header.Name}";

    private sealed record LogJob
    {
        public static LogJob DetailLine(string line) => new() { Detail = line, Header = default };

        public static LogJob InlineSml(
            long sequence,
            SecsDirection direction,
            int systemBytes,
            SecsHeader header,
            string sml) => new()
        {
            Sequence = sequence,
            Direction = direction,
            SystemBytes = systemBytes,
            Header = header,
            Sml = sml
        };

        public string? Detail { get; init; }

        public long Sequence { get; init; }

        public SecsDirection Direction { get; init; }

        public int SystemBytes { get; init; }

        public SecsHeader Header { get; init; }

        public string? Sml { get; init; }
    }
}

/// <summary>
/// 按段读 sml.log 里的一段正文。每次读到页边界前的最后一个换行处，
/// 剩下的半行留到下一页，界面拿到的永远是整行。
/// </summary>
public sealed class SmlBlockReader : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly FileStream _stream;
    private readonly Decoder _decoder = Utf8.GetDecoder();
    private readonly byte[] _buffer = new byte[256 * 1024];
    private byte[] _leftover = [];
    private long _remaining;

    private SmlBlockReader(FileStream stream, long remaining)
    {
        _stream = stream;
        _remaining = remaining;
    }

    internal static Task<SmlBlockReader> OpenAsync(
        string path,
        SmlLocation location,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            // 定位不是 IO，同步做就够了。
            stream.Seek(location.Offset, SeekOrigin.Begin);
            return Task.FromResult(new SmlBlockReader(stream, location.Length));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>读下一页整行文本；这段读完了返回 null。</summary>
    public async Task<string?> ReadPageAsync(CancellationToken cancellation = default)
    {
        if (_remaining == 0 && _leftover.Length == 0)
        {
            return null;
        }

        var toRead = (int)Math.Min(_buffer.Length, _remaining);
        var read = await _stream.ReadAsync(_buffer.AsMemory(0, toRead), cancellation).ConfigureAwait(false);
        if (read == 0)
        {
            // 文件比记录里写的长度短：日志还在内存里没落盘，或者文件被截断过。
            // 这种时候必须当成读完，否则每次都返回空页，调用方那个 while 循环永远出不来，CPU 跑满。
            _remaining = 0;
            if (_leftover.Length == 0)
            {
                return null;
            }
        }
        else
        {
            _remaining -= read;
        }

        var combined = _leftover.Length == 0
            ? _buffer.AsMemory(0, read)
            : Concat(_leftover, _buffer.AsMemory(0, read));

        // 切到页边界前的最后一个换行处；这一段里没有换行（超长单行或已经读到块尾）就整段吐出，
        // 否则最后那截会一直留在 leftover 里，读的人永远等不到 null。
        var cut = combined.Span.LastIndexOf((byte)'\n');
        var consume = cut >= 0 ? cut + 1 : combined.Length;

        _leftover = consume == combined.Length ? [] : combined.Slice(consume).ToArray();

        var page = combined.Slice(0, consume);
        var final = _remaining == 0 && _leftover.Length == 0;
        var chars = new char[_decoder.GetCharCount(page.Span, final)];
        _decoder.GetChars(page.Span, chars, final);
        return new string(chars);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    private static Memory<byte> Concat(byte[] left, Memory<byte> right)
    {
        var combined = new byte[left.Length + right.Length];
        left.CopyTo(combined, 0);
        right.Span.CopyTo(combined.AsSpan(left.Length));
        return combined;
    }
}

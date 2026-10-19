using System.Threading.Channels;
using QoderSecs.Core.Connection;

namespace QoderSecs.Tests;

/// <summary>
/// 测试用的双向字节流：写出去就是投进对端的读队列。
/// SECS-I 只认一段双向字节流，不认流底下是什么，所以拿它就能把整条握手线跑通，不用串口硬件。
/// 每一次写单独记一条，控制字节和整块不会混在一起数。
/// </summary>
internal sealed class PipeStream : Stream
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private ReadOnlyMemory<byte> _pending;

    // 写是在被测那条接收线程上发生的，断言那一头随时来数，所以这一格必须加锁读快照。
    // 直接暴露 List 让读的一头枚举，撞上的就是「Collection was modified」（这条真撞过）。
    private readonly List<byte[]> _chunks = [];
    private readonly object _chunksGate = new();

    /// <summary>每一次写出去的内容，按写的顺序一条一格（快照）。</summary>
    public byte[][] WrittenChunks()
    {
        lock (_chunksGate)
        {
            return [.. _chunks];
        }
    }

    /// <summary>数一数写出的块里有几块满足条件，加锁一次数完。</summary>
    public int CountWritten(Func<byte[], bool> match)
    {
        lock (_chunksGate)
        {
            return _chunks.Count(match);
        }
    }

    /// <summary>对端。留空就是「有人接但没有回」，脚本测试用它盯住线上写了什么。</summary>
    public PipeStream? Peer { get; set; }

    public static (PipeStream Left, PipeStream Right) CreatePair()
    {
        var left = new PipeStream();
        var right = new PipeStream();
        left.Peer = right;
        right.Peer = left;
        return (left, right);
    }

    /// <summary>模拟对端发来的字节。</summary>
    public void Deliver(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            _incoming.Writer.TryWrite(chunk);
        }
    }

    public void DeliverControl(Secs1Control control) => Deliver([(byte)control]);

    /// <summary>对端不再发了：本端读到这里返回 0，接收线程据此结束。</summary>
    public void DeliverEnd() => _incoming.Writer.TryComplete();

    /// <summary>等本端写出一个单独的控制字节。只数整条只有一个字节的写，不会被正文里的同值字节骗过。</summary>
    public Task<bool> WrittenByte(Secs1Control control, bool twice = false) =>
        WaitForChunkAsync([(byte)control], twice ? 2 : 1);

    /// <summary>等本端写出一次（twice 时两次）给定的整块字节。</summary>
    public Task<bool> WrittenBytes(byte[] frame, bool twice = false) =>
        WaitForChunkAsync(frame, twice ? 2 : 1);

    /// <summary>线上累计写出的字节，用来看整段握手顺序对不对。</summary>
    public byte[] AllWritten() => [.. WrittenChunks().SelectMany(chunk => chunk)];

    private async Task<bool> WaitForChunkAsync(byte[] want, int count, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(3);
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (CountWritten(chunk => chunk.AsSpan().SequenceEqual(want)) >= count)
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return false;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_pending.IsEmpty)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            _pending = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        var taken = Math.Min(buffer.Length, _pending.Length);
        _pending[..taken].Span.CopyTo(buffer.Span);
        _pending = _pending[taken..];
        return taken;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        DeliverFrom(buffer.AsSpan(offset, count).ToArray());

    public override void Write(ReadOnlySpan<byte> buffer) => DeliverFrom(buffer.ToArray());

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        DeliverFrom(buffer.AsSpan(offset, count).ToArray());
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        DeliverFrom(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    private void DeliverFrom(byte[] data)
    {
        lock (_chunksGate)
        {
            _chunks.Add(data);
        }

        Peer?.Deliver(data);
    }
}

internal static class Secs1TestSupport
{
    /// <summary>
    /// 测试里的超时。不能用"真协议的一半快"来当数：机器上跑着别的东西时，脚本里那颗
    /// EOT 可能要比 500 毫秒晚一点才送到，发送方就当成 T2 到点把整轮作废了——这种失败
    /// 和协议无关，纯是调度。所以这里的数是「宽松到不会因调度误判，又不至于拖死一轮」：
    /// 真要验超时那条路的用例自己写一个几百毫秒的 options，不跟着这里改。
    /// </summary>
    public static Secs1LineOptions Options(bool isEquipment, int retryCount = 2) =>
        new(
            DeviceId: 1,
            IsEquipment: isEquipment,
            InterCharacterTimeout: TimeSpan.FromMilliseconds(1000),
            ProtocolTimeout: TimeSpan.FromSeconds(2),
            InterBlockTimeout: TimeSpan.FromSeconds(3),
            RetryCount: retryCount);

    public static Secs1Message Message(int stream, int function, bool replyExpected, int systemBytes, int bodyBytes)
    {
        var body = new byte[bodyBytes];
        for (var i = 0; i < bodyBytes; i++)
        {
            body[i] = (byte)(i % 251);
        }

        return new Secs1Message(new SecsHeader(stream, function, replyExpected), systemBytes, body);
    }

    public static string Hex(byte[] data) => string.Join(' ', data.Select(b => b.ToString("X2")));

    /// <summary>
    /// 手拼一块原始字节：字段由调用方指定，校验和照样按标准算对。
    /// 测的是「算得对但字段不对」的块——真实机台完全可能发一个 S0 或 F0 过来。
    /// </summary>
    public static byte[] RawFrame(int stream, int function, bool replyExpected = false, int systemBytes = 1)
    {
        var header = new byte[]
        {
            0x00, 0x01,                                                 // R=0，设备号 1
            (byte)((replyExpected ? 0x80 : 0) | (stream & 0x7F)),       // W 位 + 流号
            (byte)function,                                             // 功能号
            0x80, 0x01,                                                 // E=1，块号 1
            (byte)(systemBytes >> 24), (byte)(systemBytes >> 16),
            (byte)(systemBytes >> 8), (byte)systemBytes,
        };

        var sum = header.Aggregate(0, (total, b) => total + b);
        return [10, .. header, (byte)(sum >> 8), (byte)sum];
    }

    /// <summary>用例自带的取消：卡住 5 秒就失败，不许把整轮测试拖死。</summary>
    private static readonly List<CancellationTokenSource> Roots = [];

    public static CancellationToken Source()
    {
        var source = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        lock (Roots)
        {
            Roots.Add(source);
        }

        return source.Token;
    }

    public static async Task WaitForTraceAsync(List<string> trace, string want)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (trace.Any(text => text.Contains(want)))
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new AssertFailedException($"日志里等不到「{want}」：{string.Join(" / ", trace)}");
    }
}

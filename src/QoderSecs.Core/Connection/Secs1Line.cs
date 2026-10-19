using System.Threading.Channels;

namespace QoderSecs.Core.Connection;

/// <summary>
/// 一条 SECS-I 线的参数。设备号在 Host 和 Equipment 两边填的是同一个数，
/// 差别只在 IsEquipment：它决定头里的方向位 R，也决定线上撞车时谁先退让。
/// T1/T2/T4 三个默认值照 secs4j 的 SecsTimeout 取（1 / 15 / 45 秒），本机没有仪器可核。
/// </summary>
public sealed record Secs1LineOptions(
    int DeviceId,
    bool IsEquipment,
    TimeSpan InterCharacterTimeout,
    TimeSpan ProtocolTimeout,
    TimeSpan InterBlockTimeout,
    int RetryCount)
{
    /// <summary>块内两个字节之间的空档（T1）。</summary>
    public static readonly TimeSpan DefaultInterCharacterTimeout = TimeSpan.FromSeconds(1);

    /// <summary>握手一步的等待（T2）：发 ENQ 等 EOT、发一块等 ACK 都用它。</summary>
    public static readonly TimeSpan DefaultProtocolTimeout = TimeSpan.FromSeconds(15);

    /// <summary>回完 ACK 之后等对方再敲下一块的门（T4）。多块消息中间的空档按这一档算。</summary>
    public static readonly TimeSpan DefaultInterBlockTimeout = TimeSpan.FromSeconds(45);

    /// <summary>整条消息重发的轮数，同一块被 NAK 或没等到 ACK 的重发次数也用它。</summary>
    public const int DefaultRetryCount = 3;

    public static Secs1LineOptions CreateDefault(int deviceId, bool isEquipment) =>
        new(
            deviceId,
            isEquipment,
            DefaultInterCharacterTimeout,
            DefaultProtocolTimeout,
            DefaultInterBlockTimeout,
            DefaultRetryCount);
}

/// <summary>一条 SECS-I 消息上线的结果。Detail 直接进界面状态栏，所以要把话说清楚。</summary>
public sealed record Secs1SendOutcome(bool Sent, string Detail);

/// <summary>
/// SECS-I 的块传输：在一段双向字节流上跑 ENQ / EOT / 一块 / ACK 这套握手，
/// 把半双工线上两个方向的发送排成先后，并把收到的块攒回消息。
/// 流是什么不重要——串口、命名管道、内存里的测试管道都行，所以整段可以不带硬件测。
/// 没做：控制块（SELECT、ENABLE 那一类）；SRQ 和 EIC 只记录不处理；双方同时发 ENQ 时不分主从，
/// 一律先收对方的再重发自己这条；多点共线时不按设备号过滤别人的通信（本程序一条线只走点对点）。
/// 接串口之前要记一件事：`SerialPort.BaseStream` 的 ReadAsync 是同步包异步，取消令牌要等下一个
/// 字节到达才生效。真接上串口时，T1/T2/T4 和关线程都得靠 `SerialPort.Close()` 来打断，
/// 本类现在的写法只在"读能立刻响应取消"的流上验过（内存管道）。
/// </summary>
public sealed class Secs1Line : IAsyncDisposable
{
    private enum Step
    {
        Done,
        Failed,
        Collided
    }

    private readonly Stream _stream;
    private readonly Secs1LineOptions _options;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly Channel<Secs1Message> _received = Channel.CreateUnbounded<Secs1Message>();
    private readonly Secs1Assembler _assembler = new();
    private readonly object _gate = new();
    private int _receivedCount;
    private TaskCompletionSource<Secs1Control>? _controlWaiter;
    private TaskCompletionSource _receiveFinished = Idle();
    private Task? _pump;
    private CancellationTokenSource? _cts;

    public Secs1Line(Stream stream, Secs1LineOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        _stream = stream;
        _options = options;
    }

    /// <summary>线上发生的一切都走这里：谁发了几块、谁 NAK 了、为什么丢掉半条消息。</summary>
    public Action<string>? TraceSink { get; init; }

    public Secs1LineOptions Options => _options;

    /// <summary>已经拼好、还等着上层取走的消息数。测试用它判断有没有收全。</summary>
    public int ReceivedCount => Volatile.Read(ref _receivedCount);

    /// <summary>起一个读线程。半双工线上所有字节都经过这一个读者，收发不会自己踩自己。</summary>
    public void Start(CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            if (_pump is not null)
            {
                throw new InvalidOperationException("这条线已经启动了。");
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            _pump = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
        }
    }

    /// <summary>取一条拼好的消息；线上没有完整消息时等。</summary>
    public async Task<Secs1Message> ReceiveMessageAsync(CancellationToken cancellation = default)
    {
        var message = await _received.Reader.ReadAsync(cancellation).ConfigureAwait(false);
        Interlocked.Decrement(ref _receivedCount);
        return message;
    }

    /// <summary>
    /// 发一条消息：ENQ 敲门，等 EOT，逐块发出去并等 ACK，全部确认才算发成功。
    /// 失败不抛异常，返回一句能直接给人看的说明——这条线随时可能被对端掐断，那是常态。
    /// </summary>
    public async Task<Secs1SendOutcome> SendAsync(Secs1Message message, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        IReadOnlyList<Secs1Block> blocks;
        try
        {
            blocks = message.ToBlocks(_options.DeviceId, _options.IsEquipment);
        }
        catch (ArgumentException exception)
        {
            // S255、F0、正文大到块号装不下：这种消息切不出能上线的块。
            // 上面承诺了「失败不抛异常」，所以在这儿收住，界面拿到的是话不是堆栈。
            return new Secs1SendOutcome(false, $"{message.Header.Signature} 上不了线：{exception.Message}");
        }

        var gateTaken = false;
        try
        {
            await _transferGate.WaitAsync(cancellation).ConfigureAwait(false);
            gateTaken = true;
            var lastDetail = string.Empty;
            var rounds = _options.RetryCount + 1;
            for (var attempt = 1; attempt <= rounds; attempt++)
            {
                // 对方正在往这头发：先让它发完，免得两头同时写字节。
                await WaitForReceiveFinishedAsync(cancellation).ConfigureAwait(false);
                var (step, detail) = await TransferOnceAsync(blocks, message, attempt, cancellation)
                    .ConfigureAwait(false);
                lastDetail = detail;

                if (step == Step.Done)
                {
                    return new Secs1SendOutcome(true, detail);
                }

                if (step == Step.Failed)
                {
                    // 单块自己已经重试过 RetryCount 次了。整条再从第一块重发，会把对面已经
                    // 确认过的块再灌一遍，所以到这里收手，把原因原样交出去。
                    Trace(detail);
                    return new Secs1SendOutcome(false, detail);
                }

                if (cancellation.IsCancellationRequested)
                {
                    return new Secs1SendOutcome(false, $"{message.Header.Signature} 没发完就断开了：{detail}");
                }

                // 只剩撞车这一种：这一轮根本没发出去，重发整条是对的。
                Trace($"{detail}先让它发完，再重发自己这条。");
            }

            return new Secs1SendOutcome(false,
                $"{message.Header.Signature} 和对方撞了 {rounds} 次还是没发出去：{lastDetail}");
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 上面承诺了失败只出一句话。取消令牌（断开或退出）和流被掐断都能从写字节那一路抛上来，
            // 这里收住，界面拿到的还是话。
            var detail = $"{message.Header.Signature} 没发完，这条线被关掉了（断开或退出）：{exception.Message}";
            Trace(detail);
            return new Secs1SendOutcome(false, detail);
        }
        finally
        {
            // 取消可能正好落在排队这一步上，那时锁没拿到，不能还。
            if (gateTaken)
            {
                _transferGate.Release();
            }
        }
    }

    private async Task<(Step Step, string Detail)> TransferOnceAsync(
        IReadOnlyList<Secs1Block> blocks,
        Secs1Message message,
        int attempt,
        CancellationToken cancellation)
    {
        var signature = message.Header.Signature;
        Trace($"发送 {signature} sys={message.SystemBytes:X8} 共{blocks.Count}块（第{attempt}轮）");

        foreach (var block in blocks)
        {
            // 每一块都要先敲门：对面发完 ACK 就退出接收状态了，secs4java 和 secs4js 都是这么发的。
            var (step, detail) = await SendBlockAsync(block, cancellation).ConfigureAwait(false);
            if (step != Step.Done)
            {
                return (step, detail);
            }
        }

        return (Step.Done, $"{signature} sys={message.SystemBytes:X8} {blocks.Count} 块全部确认。");
    }

    /// <summary>
    /// 送一块：敲一次门、写字节、等 ACK。没成就连门一起重来——secs4j 的重试也是重新发 ENQ，
    /// 因为对面回完 NAK 就退出接收状态了，不敲门直接再发一块它接不到。
    /// </summary>
    private async Task<(Step Step, string Detail)> SendBlockAsync(Secs1Block block, CancellationToken cancellation)
    {
        var buffer = block.Encode();
        var rounds = _options.RetryCount + 1;

        for (var attempt = 1; attempt <= rounds; attempt++)
        {
            var (doorStep, doorDetail) = await OpenDoorAsync(block, cancellation).ConfigureAwait(false);
            if (doorStep == Step.Collided)
            {
                return (doorStep, doorDetail);
            }

            if (doorStep == Step.Failed)
            {
                // 线被关掉时不算「这一轮没门」，重试没有意义，再敲一次只是拿一个已经取消的
                // 令牌去排队，那会当场抛 TaskCanceledException 跑到承诺之外。
                if (attempt == rounds || cancellation.IsCancellationRequested)
                {
                    return (doorStep, doorDetail);
                }

                Trace($"{doorDetail}第{attempt}次没开门，重新敲。");
                continue;
            }

            // 先把等待挂上再写字节：本地管道上对面的 ACK 可能在这两行之间就到，
            // 先写后等会把它当成「没人等的控制字节」丢掉，然后白等一轮 T2。
            var waiting = WaitForControlAsync(_options.ProtocolTimeout, cancellation);
            await WriteAsync(buffer, cancellation).ConfigureAwait(false);
            var (reply, cancelled) = await waiting.ConfigureAwait(false);

            if (cancelled)
            {
                return (Step.Failed, $"{block} 发出去之后这条线被关掉了（断开或退出），没等到 ACK。");
            }

            if (reply == Secs1Control.Ack)
            {
                return (Step.Done, $"{block} 已确认。");
            }

            if (reply == Secs1Control.Enq)
            {
                return (Step.Collided, $"{block} 发出去之后收到对方的 ENQ，半双工撞车。");
            }

            var detail = reply is null
                ? $"{block} 第{attempt}次发出去没等到 ACK（T2 到点）。"
                : $"{block} 第{attempt}次收到 {Name(reply.Value)}。";

            if (attempt == rounds || cancellation.IsCancellationRequested)
            {
                return (Step.Failed, detail);
            }

            Trace($"{detail}重新敲门再发这块。");
        }

        return (Step.Failed, $"{block} 连发 {rounds} 次没成功。");
    }

    /// <summary>发 ENQ 敲门并等 EOT。等来 ENQ 就是半双工撞车，这一轮作废。</summary>
    private async Task<(Step Step, string Detail)> OpenDoorAsync(
        Secs1Block block,
        CancellationToken cancellation)
    {
        var waiting = WaitForControlAsync(_options.ProtocolTimeout, cancellation);
        await WriteControlAsync(Secs1Control.Enq, cancellation).ConfigureAwait(false);
        var (reply, cancelled) = await waiting.ConfigureAwait(false);

        if (cancelled)
        {
            return (Step.Failed, $"{block} 敲门之后这条线被关掉了（断开或退出），没等到 EOT。");
        }

        if (reply is null)
        {
            return (Step.Failed, $"{block} 敲门后等了 T2 没等到 EOT，对方不接。");
        }

        if (reply == Secs1Control.Enq)
        {
            return (Step.Collided, $"{block} 敲门时收到对方的 ENQ，半双工撞车。");
        }

        if (reply != Secs1Control.Eot)
        {
            return (Step.Failed, $"{block} 的 ENQ 换来的是 {Name(reply.Value)}，不是 EOT。");
        }

        return (Step.Done, $"{block} 敲开门。");
    }

    private async Task PumpAsync(CancellationToken cancellation)
    {
        try
        {
            while (true)
            {
                // 这里不传 timeout：空闲时一直等，等到的 null 只会是流到尽头。
                var (raw, _) = await ReadControlByteAsync(cancellation).ConfigureAwait(false);
                if (raw is null)
                {
                    Trace("线上没有更多字节了（对端断开或端口已关闭）。");
                    break;
                }

                var control = (Secs1Control)raw.Value;
                if (control == Secs1Control.Enq)
                {
                    // 先把「我在收了」挂上去，再把这个 ENQ 交给正在等回复的发送方：
                    // 顺序反过来，发送方可能抢在这之前写出下一个字节，两头同时写就乱了。
                    // 撞车时那个 ENQ 必须交出去，让发送方本轮作废；但字节是这次读到的，
                    // 对方的块还跟在后面，所以之后照常当接收方。
                    var finished = BeginReceiving();
                    TryDeliverControl(control);
                    try
                    {
                        await ReceiveTransferAsync(cancellation).ConfigureAwait(false);
                    }
                    finally
                    {
                        EndReceiving(finished);
                    }

                    continue;
                }

                if (!TryDeliverControl(control))
                {
                    Trace(control is Secs1Control.Srq or Secs1Control.Eic
                        ? $"收到 {Name(control)}。本版不当成消息处理。"
                        : $"收到没人等的 {Name(control)}。");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // 读线程断了不能把异常抛回 Task.Run：等它的地方只认取消，
            // 别的异常会冒到调用断开的那一层，看到的是一句没头没尾的错误。
            Trace($"接收线程异常结束：{exception.GetType().Name} {exception.Message}");
        }
        finally
        {
            Abandon("接收线程结束");
            _received.Writer.TryComplete();
        }
    }

    /// <summary>当接收方：回 EOT，读一块，验算之后回 ACK 或 NAK，直到结束位那一块。</summary>
    private async Task ReceiveTransferAsync(CancellationToken cancellation)
    {
        await WriteControlAsync(Secs1Control.Eot, cancellation).ConfigureAwait(false);
        while (true)
        {
            var block = await ReadBlockAsync(cancellation).ConfigureAwait(false);
            if (block is null)
            {
                Abandon("这一块没读全，这一轮到此为止");
                return;
            }

            var outcome = _assembler.Feed(block);
            if (outcome.Problem is not null)
            {
                Trace(outcome.Problem);
            }

            if (outcome.Message is not null)
            {
                Trace($"收齐 {outcome.Message.Header.Signature} sys={outcome.Message.SystemBytes:X8} " +
                      $"正文{outcome.Message.Body.Length}字节。");
                Interlocked.Increment(ref _receivedCount);
                _received.Writer.TryWrite(outcome.Message);
                return;
            }

            // 还没到结束位：对方要再发一块，得先敲门。这一步只能自己读线上字节——读线程就是这里，
            // 挂个等待没有第二个读者会喂它。等的是 T4：回完 ACK 到对方敲下一块门之间的空档，
            // 按 E4 是单独一档，跟握手那一步的 T2 不同。
            var (next, ended) = await ReadControlByteAsync(cancellation, _options.InterBlockTimeout)
                .ConfigureAwait(false);
            if (next == (byte)Secs1Control.Enq)
            {
                await WriteControlAsync(Secs1Control.Eot, cancellation).ConfigureAwait(false);
                continue;
            }

            if (next is { } strayByte)
            {
                var stray = (Secs1Control)strayByte;
                if (TryDeliverControl(stray))
                {
                    // 这颗是替别人收的：撞车窗口里发送方也可能在等一个控制字节。交出去之后接着等
                    // 下一块的 ENQ——一颗不属于本轮的字节不该把整条消息判死。
                    Trace($"收到 {Name(stray)}，交给正在等它的人，继续等下一块的 ENQ。");
                    continue;
                }
            }

            Abandon(ended
                ? "读线上字节时流到了尽头"
                : next is null
                    ? $"等下一块的 ENQ 超过 T4（{(int)_options.InterBlockTimeout.TotalMilliseconds} 毫秒）"
                    : $"等下一块的 ENQ，先收到 {Name((Secs1Control)next.Value)}");
            return;
        }
    }

    private TaskCompletionSource BeginReceiving()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _receiveFinished = finished;
        }

        return finished;
    }

    private void EndReceiving(TaskCompletionSource finished)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_receiveFinished, finished))
            {
                _receiveFinished = Idle();
            }
        }

        finished.TrySetResult();
    }

    /// <summary>
    /// 读一个整块：长度字节等 T2（对方回完 EOT 到把块发出来，中间停几秒是正常的），
    /// 块内字节之间才等 T1。长度不合法、头不合法、校验对不上、块没读全，都扫掉失步字节再回 NAK，
    /// 结束这一轮，等对方重新敲门。
    /// </summary>
    private async Task<Secs1Block?> ReadBlockAsync(CancellationToken cancellation)
    {
        var (lengthBuffer, ended) = await ReadExactlyAsync(1, _options.ProtocolTimeout, cancellation)
            .ConfigureAwait(false);
        if (lengthBuffer is null)
        {
            if (!ended)
            {
                await RejectAsync(
                    $"等了 T2（{(int)_options.ProtocolTimeout.TotalMilliseconds} 毫秒）没等到块的长度字节",
                    cancellation).ConfigureAwait(false);
            }

            return null;
        }

        var length = lengthBuffer[0];
        if (length < Secs1Block.MinLength || length > Secs1Block.MaxLength)
        {
            await RejectAsync(
                $"长度字节 0x{length:X2} 不在 {Secs1Block.MinLength} 到 {Secs1Block.MaxLength} 之间",
                cancellation).ConfigureAwait(false);
            return null;
        }

        var (rest, restEnded) = await ReadExactlyAsync(
            length + Secs1Block.ChecksumBytes, _options.InterCharacterTimeout, cancellation).ConfigureAwait(false);
        if (rest is null)
        {
            if (!restEnded)
            {
                await RejectAsync($"这一块没读全（长度字节说头加正文该有 {length} 字节）", cancellation)
                    .ConfigureAwait(false);
            }

            return null;
        }

        var frame = new byte[length + 3];
        frame[0] = length;
        rest.CopyTo(frame, 1);

        if (!Secs1Block.TryDecode(frame, out var block, out var error))
        {
            await RejectAsync($"块解不开：{error}", cancellation).ConfigureAwait(false);
            return null;
        }

        Trace($"收到 {block}。");
        await WriteControlAsync(Secs1Control.Ack, cancellation).ConfigureAwait(false);
        return block;
    }

    /// <summary>
    /// 这一轮收不下去了。先把线上还没归属的字节扫干净再回 NAK——两头读到这儿已经对不齐了，
    /// 不扫的话这些字节会混进下一轮，secs4j 也是这个顺序。
    /// </summary>
    private async Task RejectAsync(string reason, CancellationToken cancellation)
    {
        Trace($"{reason}，回 NAK。");

        await DrainStrayBytesAsync(cancellation).ConfigureAwait(false);
        await WriteControlAsync(Secs1Control.Nak, cancellation).ConfigureAwait(false);
    }

    /// <summary>字节失步时把还没归属的字节读掉，读到线上安静下来为止（按 T1 判）。</summary>
    private async Task DrainStrayBytesAsync(CancellationToken cancellation)
    {
        var buffer = new byte[64];
        var total = 0;
        while (true)
        {
            var (read, _) = await ReadSomeAsync(
                buffer, _options.InterCharacterTimeout, cancellation, traceOnTimeout: false).ConfigureAwait(false);
            if (read is null)
            {
                break;
            }

            total += read.Value;
        }

        if (total > 0)
        {
            Trace($"扫掉 {total} 个对不上块的字节。");
        }
    }

    /// <summary>
    /// 读线上下一个字节。不给 timeout 就空闲一直等；ended 为 true 表示流到尽头（对端断开）。
    /// 给了 timeout 时控制字节为 null 而 ended 是 false，就是这一步没等到。
    /// </summary>
    private async Task<(byte? Control, bool Ended)> ReadControlByteAsync(
        CancellationToken cancellation,
        TimeSpan? timeout = null)
    {
        var buffer = new byte[1];
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (timeout is { } limit)
        {
            source.CancelAfter(limit);
        }

        int read;
        try
        {
            read = await _stream.ReadAsync(buffer, source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return (null, false);
        }

        return read == 0 ? (null, true) : (buffer[0], false);
    }

    /// <summary>
    /// 读满 count 个字节：第一个字节按 firstTimeout 等（块的长度字节等的是 T2，对方回完 EOT
    /// 到真把块发出来中间可能停几秒），后续字节之间按 T1 等。
    /// Buffer 为 null 时 Ended 说清楚是流到尽头还是这一步没等到。
    /// </summary>
    private async Task<(byte[]? Buffer, bool Ended)> ReadExactlyAsync(
        int count,
        TimeSpan firstTimeout,
        CancellationToken cancellation)
    {
        var buffer = new byte[count];
        var offset = 0;
        var wait = firstTimeout;
        while (offset < count)
        {
            var (read, ended) = await ReadSomeAsync(buffer.AsMemory(offset, count - offset), wait, cancellation)
                .ConfigureAwait(false);
            if (read is null)
            {
                return (null, ended);
            }

            offset += read.Value;

            // 第一个字节到了以后，剩下的就是块内字符间隔那一档。
            wait = _options.InterCharacterTimeout;
        }

        return (buffer, false);
    }

    /// <summary>
    /// 读一批字节。返回的条数为 null 时，Ended 是 true 表示流到了尽头，false 表示这一等超时了。
    /// traceOnTimeout 关掉时不写日志：扫失步字节就是靠"读到没动静"收尾的，每次都记一行会变成噪音。
    /// </summary>
    private async Task<(int? Count, bool Ended)> ReadSomeAsync(
        Memory<byte> buffer,
        TimeSpan timeout,
        CancellationToken cancellation,
        bool traceOnTimeout = true)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            var read = await _stream.ReadAsync(buffer, limit.Token).ConfigureAwait(false);
            return read == 0 ? (null, true) : (read, false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            if (traceOnTimeout)
            {
                Trace($"读字节超过 {(int)timeout.TotalMilliseconds} 毫秒。");
            }

            return (null, false);
        }
    }

    /// <summary>
    /// 发送方等读线程递过来的一个控制字节（握手的一步）。没等到时 Cancelled 说清楚是这一等超时了
    /// 还是这条线被关掉：界面拿到的那句话不能把用户主动断开报成「对方不回」。
    /// 同一时刻只有一个发送方在等，读线程自己收块时不走这里。
    /// </summary>
    private async Task<(Secs1Control? Control, bool Cancelled)> WaitForControlAsync(
        TimeSpan timeout,
        CancellationToken cancellation)
    {
        var waiter = new TaskCompletionSource<Secs1Control>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _controlWaiter = waiter;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeoutSource.CancelAfter(timeout);
        using var registration = timeoutSource.Token.Register(static state =>
            ((TaskCompletionSource<Secs1Control>)state!).TrySetCanceled(), waiter);

        try
        {
            return (await waiter.Task.ConfigureAwait(false), false);
        }
        catch (OperationCanceledException)
        {
            return (null, cancellation.IsCancellationRequested);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_controlWaiter, waiter))
                {
                    _controlWaiter = null;
                }
            }
        }
    }

    private bool TryDeliverControl(Secs1Control control)
    {
        TaskCompletionSource<Secs1Control>? waiter;
        lock (_gate)
        {
            waiter = _controlWaiter;
        }

        return waiter?.TrySetResult(control) == true;
    }

    private async Task WaitForReceiveFinishedAsync(CancellationToken cancellation)
    {
        Task waiting;
        lock (_gate)
        {
            waiting = _receiveFinished.Task;
        }

        await waiting.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private void Abandon(string reason)
    {
        var problem = _assembler.Abandon(reason);
        if (problem is not null)
        {
            Trace(problem);
        }
    }

    private async Task WriteControlAsync(Secs1Control control, CancellationToken cancellation) =>
        await WriteAsync([(byte)control], cancellation).ConfigureAwait(false);

    private async Task WriteAsync(byte[] data, CancellationToken cancellation)
    {
        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(data, cancellation).ConfigureAwait(false);
            await _stream.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Trace(string detail) => TraceSink?.Invoke(detail);

    private static TaskCompletionSource Idle()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    internal static string Name(Secs1Control control) => control switch
    {
        Secs1Control.Enq => "ENQ",
        Secs1Control.Eot => "EOT",
        Secs1Control.Ack => "ACK",
        Secs1Control.Nak => "NAK",
        Secs1Control.Srq => "SRQ",
        Secs1Control.Eic => "EIC",
        _ => $"0x{(byte)control:X2}"
    };

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cts;
        Task? pump;
        lock (_gate)
        {
            cts = _cts;
            pump = _pump;
            _cts = null;
            _pump = null;
        }

        cts?.Cancel();
        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // 收线时的异常不往外抛：原因已经在 TraceSink 那句「接收线程异常结束」里了。
            }
        }

        _stream.Dispose();
        _writeGate.Dispose();
        _transferGate.Dispose();
    }
}

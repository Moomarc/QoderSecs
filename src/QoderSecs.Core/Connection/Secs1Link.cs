using System.IO.Ports;
using System.Threading.Channels;
using QoderSecs.Core.Messages;
using QoderSecs.Core.Profiles;
using Secs4Net;

namespace QoderSecs.Core.Connection;

/// <summary>
/// 一条 SECS-I 连接。串口（或测试里的任意双向流）只负责搬字节，
/// ENQ/EOT/ACK 这一套在 <see cref="Secs1Line"/> 里；记录、配对、日志、自动回复
/// 和 HSMS 一样走共用的 <see cref="SecsSession"/>。
/// SECS-I 是点对点，没有 Active/Passive：谁先发都可以。
/// </summary>
public sealed class Secs1Link : IAsyncDisposable, ISecsTransportLink
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SecsSession _session;
    private readonly Dictionary<int, TaskCompletionSource<SecsHeader>> _replyWaiters = [];
    private bool _slotTaken;
    private CancellationTokenSource? _pendingSend;
    private CancellationTokenSource? _cts;
    private SerialPort? _port;
    private Secs1Line? _line;
    private Task? _receiveTask;
    private int _nextSystemBytes = Random.Shared.Next(1, int.MaxValue);

    public Secs1Link()
        : this(new SecsSession())
    {
    }

    /// <summary>ConnectionLink 把同一份会话交给 HSMS 和 SECS-I 共用。</summary>
    internal Secs1Link(SecsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>测试替身：不打开串口，直接用一对内存管道。设了它就不碰 SerialPort。</summary>
    internal Func<ConnectionProfile, Stream>? StreamFactory { get; init; }

    public TranscriptCorrelator Transcript => _session.Transcript;

    public Func<MessageLibrary?>? LibraryProvider
    {
        get => _session.LibraryProvider;
        set => _session.LibraryProvider = value;
    }

    public TranscriptLog? Log => _session.Log;

    public Func<ConnectionProfile, string>? SessionDirectoryFactory
    {
        get => _session.SessionDirectoryFactory;
        set => _session.SessionDirectoryFactory = value;
    }

    public LinkPhase Phase => _session.Phase;

    public ConnectionProfile? Profile { get; private set; }

    public event EventHandler<LinkPhase>? PhaseChanged
    {
        add => _session.PhaseChanged += value;
        remove => _session.PhaseChanged -= value;
    }

    public bool CanConnect
    {
        get
        {
            lock (_gate)
            {
                return !_slotTaken;
            }
        }
    }

    public void Connect(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Transport != TransportKind.Secs1)
        {
            throw new ArgumentException("这份连接参数不是 SECS-I 的。", nameof(profile));
        }

        var errors = ConnectionProfileValidator.Validate(profile);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(profile));
        }

        lock (_gate)
        {
            if (_slotTaken)
            {
                throw new InvalidOperationException("当前已经有一条连接，请先断开。");
            }

            // 串口要在会话打开之后才动，先占位，免得两次点击同时进来。
            _slotTaken = true;
        }

        try
        {
            _session.Open(
                profile,
                "SECS-I",
                $"角色={profile.Role} 串口={profile.SerialPortName} " +
                $"{profile.BaudRate} {profile.DataBits}{ParityLetter(profile.Parity)}{StopBitsText(profile.StopBits)} " +
                $"流控={FlowControlText(profile.FlowControl)} DeviceId={profile.DeviceId} " +
                $"T1={profile.T1Seconds}s T2={profile.T2Seconds}s T3={profile.T3Seconds}s T4={profile.T4Seconds}s");
            _session.SetPhase(LinkPhase.Connecting);

            var options = new Secs1LineOptions(
                profile.DeviceId,
                profile.Role == EndpointRole.Equipment,
                TimeSpan.FromSeconds(profile.T1Seconds),
                TimeSpan.FromSeconds(profile.T2Seconds),
                TimeSpan.FromSeconds(profile.T4Seconds),
                Secs1LineOptions.DefaultRetryCount);

            SerialPort? port = null;
            Stream stream;
            if (StreamFactory is { } factory)
            {
                stream = factory(profile);
            }
            else
            {
                port = CreateSerialPort(profile);
                port.Open();
                stream = port.BaseStream;
            }

            var line = new Secs1Line(stream, options)
            {
                TraceSink = detail => _session.Log?.EnqueueDetail($"线路 {detail}")
            };
            var cts = new CancellationTokenSource();

            lock (_gate)
            {
                _port = port;
                _line = line;
                _cts = cts;
                Profile = profile;
            }

            line.Start(cts.Token);
            _session.SetPhase(LinkPhase.Online);
            _receiveTask = Task.Run(() => ReceivePumpAsync(line, cts.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            SerialPort? failedPort;
            Secs1Line? failedLine;
            CancellationTokenSource? failedCts;
            Task? failedReceiveTask;
            lock (_gate)
            {
                failedPort = _port;
                failedLine = _line;
                failedCts = _cts;
                failedReceiveTask = _receiveTask;
                _port = null;
                _line = null;
                _cts = null;
                _receiveTask = null;
                _slotTaken = false;
                Profile = null;
            }

            _session.AbortOpenAsync($"会话开始失败：{ex.Message}").AsTask().GetAwaiter().GetResult();
            _session.SetPhase(LinkPhase.Offline);
            _ = CleanupDetachedLineAsync(failedPort, failedLine, failedCts, failedReceiveTask);
            throw new InvalidOperationException(DescribeStartFailure(profile, ex), ex);
        }
    }

    public async ValueTask DisconnectAsync()
    {
        SerialPort? port;
        Secs1Line? line;
        CancellationTokenSource? cts;
        Task? receiveTask;

        lock (_gate)
        {
            port = _port;
            line = _line;
            cts = _cts;
            receiveTask = _receiveTask;
            _port = null;
            _line = null;
            _cts = null;
            _receiveTask = null;
            _slotTaken = false;
            _replyWaiters.Clear();
            Profile = null;
        }

        if (line is null)
        {
            await _session.CloseAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            _session.SetPhase(LinkPhase.Disconnecting);
            cts?.Cancel();

            // 串口读是同步包异步，取消令牌不会打断已经挂住的 Read，先关端口让它返回。
            if (port is not null)
            {
                try
                {
                    port.Close();
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    // 端口可能已经在对端断开时被系统关掉了，收尾照样往下走。
                }
            }

            if (receiveTask is not null)
            {
                try
                {
                    await receiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            if (line is not null)
            {
                await line.DisposeAsync().ConfigureAwait(false);
            }

            port?.Dispose();

            // 等回复的那一条已经随 cts 取消；这里清表只是防止残留。
            lock (_gate)
            {
                _replyWaiters.Clear();
            }

            await _session.CloseAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => DisconnectAsync();

    /// <summary>
    /// 发送一条消息。带 W 位时等同 SystemBytes 的 Secondary，T3 到点、断开或取消等待
    /// 都会叫停，三种落定的说法和 HSMS 一致，界面不用知道底下是哪条传输。
    /// </summary>
    public async Task<string> SendAsync(SecsMessage message, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        Secs1Line line;
        ConnectionProfile profile;
        CancellationTokenSource linkCancellation;
        lock (_gate)
        {
            if (_line is null || _cts is null || Profile is null)
            {
                throw new InvalidOperationException("尚未连接，不能发送。");
            }

            line = _line;
            profile = Profile;
            linkCancellation = _cts;
        }

        var header = new SecsHeader(message.S, message.F, message.ReplyExpected, message.Name);
        var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, linkCancellation.Token);
        try
        {
            await _sendGate.WaitAsync(sendCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            sendCancellation.Dispose();
            return NotSentText(header, linkCancellation);
        }

        lock (_gate)
        {
            _pendingSend = sendCancellation;
        }

        var systemBytes = NextSystemBytes();
        var record = _session.Record(SecsDirection.Out, systemBytes, message);
        TaskCompletionSource<SecsHeader>? waiter = null;

        try
        {
            if (header.ReplyExpected && !header.IsSecondary)
            {
                waiter = new TaskCompletionSource<SecsHeader>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    _replyWaiters[systemBytes] = waiter;
                }
            }

            var outcome = await line.SendAsync(Secs1Payload.ToFrame(message, systemBytes), sendCancellation.Token)
                .ConfigureAwait(false);
            if (!outcome.Sent)
            {
                RemoveWaiter(systemBytes, waiter);
                _session.Transcript.MarkSendFailed(record);
                _session.Log?.EnqueueDetail(
                    $"未发出 {header.Signature} sys={systemBytes:X8}：{outcome.Detail}");
                return $"{header.Signature} 发送失败：{outcome.Detail}";
            }

            if (waiter is null)
            {
                return $"{SecsSession.SentPrefix} {header.Signature}。";
            }

            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(sendCancellation.Token);
            waitCancellation.CancelAfter(TimeSpan.FromSeconds(profile.T3Seconds));
            try
            {
                var replyHeader = await waiter.Task.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
                return $"{SecsSession.SentPrefix} {header.Signature}，收到 {replyHeader.Signature}。";
            }
            catch (OperationCanceledException) when (!sendCancellation.IsCancellationRequested)
            {
                RemoveWaiter(systemBytes, waiter);
                _session.Transcript.MarkTimedOut(systemBytes, DateTimeOffset.Now);
                _session.Log?.EnqueueDetail(
                    $"T3 超时 {header.Signature} sys={systemBytes:X8}：等待回复超过 {profile.T3Seconds} 秒");
                return $"{header.Signature} 等待回复超时（T3 {profile.T3Seconds} 秒）。";
            }
            catch (OperationCanceledException)
            {
                RemoveWaiter(systemBytes, waiter);
                return AbandonWaitText(header, systemBytes, linkCancellation);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingSend, sendCancellation))
                {
                    _pendingSend = null;
                }

                sendCancellation.Dispose();
            }

            _sendGate.Release();
        }
    }

    /// <summary>和 HSMS 同一条口径：只叫停「等」，消息已经在线上了。</summary>
    public bool CancelPendingSend()
    {
        lock (_gate)
        {
            if (_pendingSend is null)
            {
                return false;
            }

            _pendingSend.Cancel();
            return true;
        }
    }

    private string AbandonWaitText(SecsHeader header, int systemBytes, CancellationTokenSource linkCancellation)
    {
        if (linkCancellation.IsCancellationRequested)
        {
            _session.Log?.EnqueueDetail($"连接已断开，不再等 {header.Signature} sys={systemBytes:X8} 的回复");
            return $"{SecsSession.SentPrefix} {header.Signature}，连接已断开，没等到回复。";
        }

        _session.Transcript.MarkWaitAbandoned(systemBytes, DateTimeOffset.Now);
        _session.Log?.EnqueueDetail($"取消等待 {header.Signature} sys={systemBytes:X8} 的回复");
        return $"{SecsSession.SentPrefix} {header.Signature}，已取消等待回复。";
    }

    private static string NotSentText(SecsHeader header, CancellationTokenSource linkCancellation) =>
        $"{header.Signature} 未发出：{(linkCancellation.IsCancellationRequested ? "连接已断开" : "已取消等待")}。";

    private async Task ReceivePumpAsync(Secs1Line line, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var frame = await line.ReceiveMessageAsync(cancellation).ConfigureAwait(false);
                if (!Secs1Payload.TryToMessage(frame, out var message, out var error) || message is null)
                {
                    _session.Log?.EnqueueDetail(
                        $"收到 {frame.Header.Signature} sys={frame.SystemBytes:X8} 正文解不开：{error}");
                    continue;
                }

                using (message)
                {
                    var header = new SecsHeader(message.S, message.F, message.ReplyExpected, message.Name);
                    _session.Record(SecsDirection.In, frame.SystemBytes, message);

                    if (header.IsSecondary)
                    {
                        DeliverReply(frame.SystemBytes, header);
                        continue;
                    }

                    await _session.AutoReplyAsync(
                            message,
                            (reply, token) => SendAutoReplyAsync(line, frame.SystemBytes, reply, token),
                            cancellation)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
            // 线路自己结束了（对端把串口关了、USB 转接头被拔掉），不是本端在断开。
            // 不把阶段改过来，界面会一直显示「在线」，下一次发送才莫名其妙地失败。
            if (!cancellation.IsCancellationRequested)
            {
                _session.Log?.EnqueueDetail("串口不再有数据：对端已断开或端口被拔掉。");
                _session.SetPhase(LinkPhase.PeerLost);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            _session.Log?.EnqueueDetail(
                $"接收管线异常结束：{exception.GetType().Name} {exception.Message}");
        }
    }

    /// <summary>
    /// 自动回复走线路自己的发送，不占用户发送的闸门：用户在等回复时，
    /// 本端照样要能回对方发来的 Primary。回出去之后记一条 Out，
    /// 共用关联器会把收到的那条 Primary 从「待本端回复」改成「已自动回复」。
    /// </summary>
    private async Task<bool> SendAutoReplyAsync(
        Secs1Line line,
        int systemBytes,
        SecsMessage reply,
        CancellationToken cancellation)
    {
        var outcome = await line.SendAsync(Secs1Payload.ToFrame(reply, systemBytes), cancellation)
            .ConfigureAwait(false);
        if (!outcome.Sent)
        {
            _session.Log?.EnqueueDetail($"自动回复没能上线 sys={systemBytes:X8}：{outcome.Detail}");
            return false;
        }

        _session.Record(SecsDirection.Out, systemBytes, reply);
        return true;
    }

    private void DeliverReply(int systemBytes, SecsHeader header)
    {
        TaskCompletionSource<SecsHeader>? waiter = null;
        lock (_gate)
        {
            if (_replyWaiters.Remove(systemBytes, out var found))
            {
                waiter = found;
            }
        }

        waiter?.TrySetResult(header);
    }

    private void RemoveWaiter(int systemBytes, TaskCompletionSource<SecsHeader>? waiter)
    {
        if (waiter is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_replyWaiters.TryGetValue(systemBytes, out var current) && ReferenceEquals(current, waiter))
            {
                _replyWaiters.Remove(systemBytes);
            }
        }
    }

    private int NextSystemBytes()
    {
        while (true)
        {
            var candidate = Interlocked.Increment(ref _nextSystemBytes);
            if (candidate != 0)
            {
                return candidate;
            }
        }
    }

    private static SerialPort CreateSerialPort(ConnectionProfile profile) =>
        new(
            profile.SerialPortName.Trim(),
            profile.BaudRate,
            profile.Parity switch
            {
                SerialParity.Odd => Parity.Odd,
                SerialParity.Even => Parity.Even,
                SerialParity.Mark => Parity.Mark,
                SerialParity.Space => Parity.Space,
                _ => Parity.None
            },
            profile.DataBits,
            profile.StopBits switch
            {
                SerialStopBits.Two => StopBits.Two,
                SerialStopBits.OnePointFive => StopBits.OnePointFive,
                _ => StopBits.One
            })
        {
            Handshake = profile.FlowControl switch
            {
                SerialFlowControl.RtsCts => Handshake.RequestToSend,
                SerialFlowControl.XOnXOff => Handshake.XOnXOff,
                _ => Handshake.None
            },
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 10000,
            DtrEnable = true,
            RtsEnable = true
        };

    private static string ParityLetter(SerialParity parity) => parity switch
    {
        SerialParity.Odd => "O",
        SerialParity.Even => "E",
        SerialParity.Mark => "M",
        SerialParity.Space => "S",
        _ => "N"
    };

    private static string StopBitsText(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.Two => "2",
        SerialStopBits.OnePointFive => "1.5",
        _ => "1"
    };

    private static string FlowControlText(SerialFlowControl flowControl) => flowControl switch
    {
        SerialFlowControl.RtsCts => "RTS/CTS",
        SerialFlowControl.XOnXOff => "XON/XOFF",
        _ => "无"
    };

    private static string DescribeStartFailure(ConnectionProfile profile, Exception ex) =>
        $"无法打开串口 {profile.SerialPortName}：{ex.Message}";

    /// <summary>Start 失败后的收尾：字段已经摘掉，这里只碰局部引用，不干扰下一次连接。</summary>
    private static async Task CleanupDetachedLineAsync(
        SerialPort? port,
        Secs1Line? line,
        CancellationTokenSource? cts,
        Task? receiveTask)
    {
        try
        {
            cts?.Cancel();
            port?.Close();
            if (receiveTask is not null)
            {
                await receiveTask.ConfigureAwait(false);
            }

            if (line is not null)
            {
                await line.DisposeAsync().ConfigureAwait(false);
            }

            port?.Dispose();
        }
        catch (Exception)
        {
            // 真正的原因由 Connect 抛出的异常给出。
        }
    }
}

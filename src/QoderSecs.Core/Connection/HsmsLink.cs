using Microsoft.Extensions.Options;
using QoderSecs.Core.Messages;
using QoderSecs.Core.Profiles;
using Secs4Net;

namespace QoderSecs.Core.Connection;

/// <summary>
/// 一条 HSMS 连接。Role 只影响 profile 里的配置，Host 和 Equipment 走同一套代码。
/// 通信记录来自 Secs4Net 的收发回调，因此孤立和迟到的 Secondary 也能看到。
/// </summary>
public sealed class HsmsLink : IAsyncDisposable, ISecsTransportLink
{
    /// <summary>
    /// 发送成功的状态文本一律以这个词开头，其余（超时、发送失败）都是坏消息。
    /// 界面靠它决定状态栏要不要标红，所以措辞要改就改这一处，别两边各写一遍。
    /// </summary>
    public const string SentPrefix = SecsSession.SentPrefix;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SecsSession _session;
    private bool _slotTaken;

    public HsmsLink()
        : this(new SecsSession())
    {
    }

    /// <summary>ConnectionLink 把同一份会话交给 HSMS 和 SECS-I 共用，记录和日志才不会分成两摞。</summary>
    internal HsmsLink(SecsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>本端正在主动断开。Secs4Net 会把断开时的取消当错误报上来，靠这个区分措辞。</summary>
    private bool _closing;

    /// <summary>
    /// 在途那一条发送的取消源。断开和「取消等待」都经它叫停等回复，
    /// 取、消、放都在 _gate 里做，界面点取消不会碰上已释放的取消源。
    /// </summary>
    private CancellationTokenSource? _pendingSend;
    private CancellationTokenSource? _cts;
    private HsmsConnection? _connection;
    private SecsGem? _gem;
    private Task? _receiveTask;
    private SecsMessage? _inFlightMessage;
    private int? _inFlightSystemBytes;

    public TranscriptCorrelator Transcript => _session.Transcript;

    /// <summary>当前消息库。界面载入新文件时会整份换掉实例，所以每次要回复时现取。</summary>
    public Func<MessageLibrary?>? LibraryProvider
    {
        get => _session.LibraryProvider;
        set => _session.LibraryProvider = value;
    }

    /// <summary>本次连接的磁盘日志；连接时创建，断开时关闭。没连接时为 null。</summary>
    public TranscriptLog? Log => _session.Log;

    /// <summary>
    /// 会话日志目录怎么来。默认放在 exe 同目录的 QoderSecsData/logs 下按配置和进程区分；
    /// 测试用它指到临时目录。
    /// </summary>
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

        // 参数错误应该在占用连接之前报出来。
        var options = SecsGemOptionsFactory.Create(profile);

        lock (_gate)
        {
            if (_slotTaken)
            {
                throw new InvalidOperationException("当前已经有一条连接，请先断开。");
            }

            // Passive 模式在 HsmsConnection 构造时就会绑定端口，所以要先占位。
            _slotTaken = true;
            _closing = false;
        }

        try
        {
            // 记录、日志、配对和自动回复都在共用会话单元里，HSMS 只交自己的连接说明。
            _session.Open(
                profile,
                "HSMS",
                $"角色={profile.Role} 模式={profile.Mode} " +
                $"地址={profile.IpAddress}:{profile.Port} DeviceId={profile.DeviceId} T3={profile.T3Seconds}s");

            var holder = Options.Create(options);
            var logger = new TranscriptLogger(this);
            var connection = new HsmsConnection(holder, logger);
            var gem = new SecsGem(holder, connection, logger);
            var cts = new CancellationTokenSource();

            connection.ConnectionChanged += OnConnectionChanged;
            lock (_gate)
            {
                _connection = connection;
                _gem = gem;
                _cts = cts;
                Profile = profile;
            }

            connection.Start(cts.Token);
            _receiveTask = Task.Run(() => PumpAsync(gem, cts.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 字段要在抛出之前同步摘干净：端口清理是后台跑的，
            // 用户改完 IP 马上重点「连接」时，新的连接不能被这次残留的清理碰掉。
            SecsGem? failedGem;
            HsmsConnection? failedConnection;
            CancellationTokenSource? failedCts;
            Task? failedReceiveTask;
            lock (_gate)
            {
                failedGem = _gem;
                failedConnection = _connection;
                failedCts = _cts;
                failedReceiveTask = _receiveTask;
                _gem = null;
                _connection = null;
                _cts = null;
                _receiveTask = null;
                _slotTaken = false;
                Profile = null;
            }

            _session.AbortOpenAsync($"会话开始失败：{ex.Message}").AsTask().GetAwaiter().GetResult();
            _session.SetPhase(LinkPhase.Offline);
            _ = CleanupDetachedLinkAsync(failedGem, failedConnection, failedCts, failedReceiveTask);
            throw new InvalidOperationException(DescribeStartFailure(profile, ex), ex);
        }
    }

    public async ValueTask DisconnectAsync()
    {
        HsmsConnection? connection;
        SecsGem? gem;
        CancellationTokenSource? cts;
        Task? receiveTask;

        lock (_gate)
        {
            connection = _connection;
            gem = _gem;
            cts = _cts;
            receiveTask = _receiveTask;
            _connection = null;
            _gem = null;
            _cts = null;
            _receiveTask = null;
            _slotTaken = false;
            _closing = true;
            Profile = null;
        }

        if (connection is null)
        {
            // 没有活连接（没连过、或已经断过）：只剩收尾。
            // CloseAsync 会先把还挂着「等待回复」的记录落到「连接已断开」，再关日志。
            await _session.CloseAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            _session.SetPhase(LinkPhase.Disconnecting);
            cts?.Cancel();

            if (receiveTask is not null)
            {
                try
                {
                    await receiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 接收管线因断开而结束属于正常路径。
                }
            }
        }
        finally
        {
            // 清理必须跑完：漏掉 connection.DisposeAsync()，Passive 模式下端口会一直被占着，
            // 再点连接只会得到「地址已在使用」，用户看不出来是上一次没清干净。
            Exception? teardownFailure = null;
            try
            {
                gem?.Dispose();
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // 实测：收管线已被取消后，Secs4Net 的 HsmsConnection.Disconnect 自己会抛
                // NullReferenceException。断开这一步其实成功了（端口释放、记录落定），
                // 让它抛出去只会让界面弹「这一步操作已中止」。原因在对方库里，我们改不了，
                // 所以按 Start 失败后清理的同一口径处理：吃掉，但记进本次会话日志。
                teardownFailure = exception;
            }

            if (teardownFailure is not null)
            {
                Log?.EnqueueDetail(
                    $"连接对象收尾时抛了 {teardownFailure.GetType().Name}：{teardownFailure.Message}；本端已按断开处理。");
            }

            // 把还挂着的记录落到「连接已断开」，这几条状态变化要写进本次会话的 detail.log，
            // 所以日志留到最后再关。
            await _session.CloseAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => DisconnectAsync();

    /// <summary>
    /// 发送一条消息。带 W 位时等对方回复，T3 到点、本端断开或取消等待都会叫停，
    /// 三种落定的说法不一样，所以返回值直接写进界面状态栏。
    /// </summary>
    public async Task<string> SendAsync(SecsMessage message, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        SecsGem gem;
        CancellationTokenSource linkCancellation;
        lock (_gate)
        {
            if (_gem is null || _cts is null)
            {
                throw new InvalidOperationException("尚未连接，不能发送。");
            }

            gem = _gem;
            linkCancellation = _cts;
        }

        // 等回复必须连着这条连接的生命周期：Secs4Net 只在回复到达或 T3 到点时落定，
        // T3 能配到 86400 秒，不取消就把发送闸门一直握着，界面会读成「按键变灰不能操作」。
        var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, linkCancellation.Token);
        var header = new SecsHeader(message.S, message.F, message.ReplyExpected, message.Name);
        try
        {
            await _sendGate.WaitAsync(sendCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            sendCancellation.Dispose();
            return NotSentText(header, linkCancellation);
        }

        // 拿到闸门才算在途：闸门一次只放一条出去，所以「取消等待」取消的必然是正在等回复的那一条，
        // 不会误伤还排在后面的发送。
        lock (_gate)
        {
            _pendingSend = sendCancellation;
        }

        try
        {
            // 出站回调对所有消息都会触发（阶段 4 的自动回复也是），只能靠引用相等认领本次请求。
            lock (_gate)
            {
                _inFlightMessage = message;
                _inFlightSystemBytes = null;
            }

            SecsMessage? reply;
            try
            {
                // 编码和出站回调都在 gem.SendAsync 的同步段里跑，大消息的 SML 落盘也在那里，
                // 所以整段扔到线程池上：界面调进来时不能被这条消息的体积卡住。
                reply = await Task.Run(() => gem.SendAsync(message, sendCancellation.Token), sendCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SecsException or OperationCanceledException or ObjectDisposedException)
            {
                int? systemBytes;
                lock (_gate)
                {
                    systemBytes = _inFlightSystemBytes;
                }

                if (exception.Message.Contains("T3", StringComparison.OrdinalIgnoreCase) && systemBytes is { } timedOut)
                {
                    Transcript.MarkTimedOut(timedOut, DateTimeOffset.Now);
                    Log?.EnqueueDetail(
                        $"T3 超时 {header.Signature} sys={timedOut:X8}：等待回复超过 {Profile?.T3Seconds ?? 0} 秒");
                    return $"{header.Signature} 等待回复超时（T3 {Profile?.T3Seconds ?? 0} 秒）。";
                }

                if (exception is OperationCanceledException)
                {
                    if (systemBytes is { } abandoned)
                    {
                        return AbandonWaitText(header, abandoned, linkCancellation);
                    }

                    // 还没上线就被叫停（排在闸门后面、或断开正好赶在发送之前）。
                    Log?.EnqueueDetail($"未发出 {header.Signature}：{StopReason(linkCancellation)}");
                    return NotSentText(header, linkCancellation);
                }

                Log?.EnqueueDetail($"发送失败 {header.Signature}：{exception.Message}");
                return $"{header.Signature} 发送失败：{exception.Message}";
            }

            if (reply is null)
            {
                return $"{SentPrefix} {header.Signature}。";
            }

            using (reply)
            {
                var replyHeader = new SecsHeader(reply.S, reply.F, reply.ReplyExpected);
                return $"{SentPrefix} {header.Signature}，收到 {replyHeader.Signature}。";
            }
        }
        finally
        {
            lock (_gate)
            {
                _inFlightMessage = null;
            }

            DisposePendingSend(sendCancellation);
            _sendGate.Release();
        }
    }

    /// <summary>
    /// 取消正在等回复的那条发送。消息已经上线了，取消掉的只是「等」这件事：
    /// 记录落到已取消等待，闸门立刻放开，发送按键跟着恢复。没有等待中的发送时返回 false。
    /// </summary>
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

    private void DisposePendingSend(CancellationTokenSource sendCancellation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pendingSend, sendCancellation))
            {
                _pendingSend = null;
            }

            // 取消源和它的释放都在同一把锁里，CancelPendingSend 不会碰见已释放的对象。
            sendCancellation.Dispose();
        }
    }

    private string AbandonWaitText(SecsHeader header, int systemBytes, CancellationTokenSource linkCancellation)
    {
        // 两种叫停都以「已发出」开头：消息确实上线了，界面据此不写成红色报错。
        // 断开叫停的不改写记录：DisconnectAsync 收尾时统一落到「连接已断开」。
        if (linkCancellation.IsCancellationRequested)
        {
            Log?.EnqueueDetail($"连接已断开，不再等 {header.Signature} sys={systemBytes:X8} 的回复");
            return $"{SentPrefix} {header.Signature}，连接已断开，没等到回复。";
        }

        Transcript.MarkWaitAbandoned(systemBytes, DateTimeOffset.Now);
        Log?.EnqueueDetail($"取消等待 {header.Signature} sys={systemBytes:X8} 的回复");
        return $"{SentPrefix} {header.Signature}，已取消等待回复。";
    }

    private static string NotSentText(SecsHeader header, CancellationTokenSource linkCancellation) =>
        $"{header.Signature} 未发出：{StopReason(linkCancellation)}。";

    private static string StopReason(CancellationTokenSource linkCancellation) =>
        linkCancellation.IsCancellationRequested ? "连接已断开" : "已取消等待";


    /// <summary>
    /// Start 失败后的端口清理：字段已经在抛出前摘掉了，这里只碰局部引用，不会干扰下一次连接。
    /// </summary>
    private static async Task CleanupDetachedLinkAsync(
        SecsGem? gem,
        HsmsConnection? connection,
        CancellationTokenSource? cts,
        Task? receiveTask)
    {
        try
        {
            cts?.Cancel();
            if (receiveTask is not null)
            {
                await receiveTask.ConfigureAwait(false);
            }

            gem?.Dispose();
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Start 已经失败，清理时的异常不携带额外信息，真正的原因由 Start 的异常给出。
        }
    }

    private static string DescribeStartFailure(ConnectionProfile profile, Exception ex) =>
        profile.Mode == ConnectionMode.Passive
            ? $"无法在 {profile.IpAddress}:{profile.Port} 监听：{ex.Message}"
            : $"无法连接 {profile.IpAddress}:{profile.Port}：{ex.Message}";

    private void OnConnectionChanged(object? sender, ConnectionState state)
    {
        var phase = state switch
        {
            ConnectionState.Connecting => LinkPhase.Connecting,
            ConnectionState.Connected => LinkPhase.Connected,
            ConnectionState.Selected => LinkPhase.Online,
            ConnectionState.Retry => LinkPhase.Retrying,
            _ => LinkPhase.Offline
        };

        bool closing;
        lock (_gate)
        {
            closing = _closing;
        }

        // 上过线又掉回 Connecting/Retry，是对端走了，不是本端正在发起连接。
        // 这时候显示"连接中"会让人以为还在连，其实这条连接已经废了，槽位还占着。
        if (!closing && _session.WasOnline && phase is LinkPhase.Connecting or LinkPhase.Retrying)
        {
            phase = LinkPhase.PeerLost;
        }

        // 本端正在断开时，Secs4Net 途中可能报一次 Retry（时序不定）。那不是"正在重连"，
        // 放上去只会把界面刚写好的「已断开。」盖成「正在按 T5 重试」。
        if (closing && phase != LinkPhase.Offline)
        {
            return;
        }

        _session.SetPhase(phase);
    }

    private async Task PumpAsync(ISecsGem gem, CancellationToken cancellation)
    {
        try
        {
            await foreach (var request in gem.GetPrimaryMessageAsync(cancellation).ConfigureAwait(false))
            {
                // 串行：一条回完再收下一条。模拟器不需要并发回复。
                await ReplyAsync(request, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            // 管线自己断了也不能把异常抛回 Task.Run：断开时 await receiveTask 只认 OperationCanceledException，
            // 别的异常会从 DisconnectAsync 冒到界面上，用户看到的是一句莫名其妙的错误。
            Log?.EnqueueDetail($"接收管线异常结束：{exception.GetType().Name} {exception.Message}");
        }
    }

    /// <summary>
    /// 只有库里给这条 SxFy 配了 AutoReply 才回；回不出去就把记录标成未自动回复。
    /// 选取、转换和日志都在共用会话单元里，这里只交出 Secs4Net 的事务回复。
    /// </summary>
    private async Task ReplyAsync(PrimaryMessageWrapper request, CancellationToken cancellation)
    {
        using var primary = request.PrimaryMessage;
        await _session.AutoReplyAsync(
                primary,
                (reply, token) => request.TryReplyAsync(reply, token),
                cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 记一条收发。记录本身在共用会话单元里做；HSMS 这边多一步：
    /// 认出这次发送的对象引用，把 Secs4Net 分配的 SystemBytes 收进在途信息，
    /// T3 超时时才有东西可以标。
    /// </summary>
    private void Record(SecsDirection direction, int systemBytes, SecsMessage message)
    {
        _session.Record(direction, systemBytes, message);

        if (direction == SecsDirection.Out)
        {
            lock (_gate)
            {
                if (ReferenceEquals(message, _inFlightMessage))
                {
                    _inFlightSystemBytes = systemBytes;
                }
            }
        }
    }

    /// <summary>
    /// Secs4Net 把本端主动断开时的取消也当错误报上来（Passive 侧一次断开能报两回），
    /// 那是我们自己 Cancel 接收循环的后果，不是故障，断开窗口里直接丢掉。
    /// 对方断开之后 Secs4Net 仍在按 T5 等对端回来，期间的拆线异常和重连被拒都归到
    /// 「对方断开后」，和界面上的「对方已断开」对上；其余报错原文一个字不少地保留。
    /// </summary>
    internal void ReportLinkError(string message, Exception? exception, SecsMessage? secsMessage)
    {
        bool closing;
        bool peerLost;
        lock (_gate)
        {
            closing = _closing;
            peerLost = Phase == LinkPhase.PeerLost;
        }

        if (closing && IsSelfCancellation(message, exception))
        {
            return;
        }

        var prefix = closing ? "断开时" : peerLost ? "对方断开后" : "异常";

        Log?.EnqueueDetail(
            $"{prefix} {message}" +
            (secsMessage is null ? string.Empty : $" S{secsMessage.S}F{secsMessage.F}") +
            (exception is null ? string.Empty : $"：{exception.GetType().Name} {exception.Message}"));
    }

    /// <summary>取消异常的报文是固定英文，Secs4Net 有时只给字符串不给异常对象，两种都要认。</summary>
    private static bool IsSelfCancellation(string message, Exception? exception) =>
        exception is OperationCanceledException ||
        message.Contains("The operation was canceled.", StringComparison.Ordinal);

    private sealed class TranscriptLogger(HsmsLink link) : ISecsGemLogger
    {
        public void Debug(string msg)
        {
        }

        public void Info(string msg)
        {
        }

        public void Warning(string msg) => link.Log?.EnqueueDetail($"警告 {msg}");

        public void Error(string msg) => link.ReportLinkError(msg, null, null);

        public void Error(string msg, Exception ex) => link.ReportLinkError(msg, ex, null);

        public void Error(string msg, SecsMessage? message, Exception? ex) =>
            link.ReportLinkError(msg, ex, message);

        public void MessageIn(SecsMessage msg, int id) => link.Record(SecsDirection.In, id, msg);

        public void MessageOut(SecsMessage msg, int id) => link.Record(SecsDirection.Out, id, msg);
    }
}

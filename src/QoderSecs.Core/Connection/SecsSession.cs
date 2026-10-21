using System.Text;
using System.Text.Json;
using QoderSecs.Core.Messages;
using QoderSecs.Core.Profiles;
using Secs4Net;

namespace QoderSecs.Core.Connection;

/// <summary>
/// 一条 SECS 会话里与传输方式无关的那一层：通信记录、SystemBytes 配对、
/// 会话日志（sml.log / detail.log / effective-profile.json）、自动回复的选取与落定。
/// HSMS 和 SECS-I 各管各的连接与收发，记录和落盘都从这里走，两边的日志口径才一致。
/// </summary>
public sealed class SecsSession
{
    /// <summary>
    /// 发送成功的状态文本一律以这个词开头，其余（超时、发送失败）都是坏消息。
    /// 界面靠它决定状态栏要不要标红，所以措辞要改就改这一处，别两边各写一遍。
    /// </summary>
    public const string SentPrefix = "已发出";

    private readonly object _gate = new();

    // SecsMessage 不公开 SystemBytes，只能靠对象引用把收发回调看到的 id 交给自动回复。
    private readonly Dictionary<SecsMessage, int> _inboundRequests = new(ReferenceEqualityComparer.Instance);

    /// <summary>这条会话上过线没有。上过线之后再掉线是对端走了，和"正在连接"是两件事。</summary>
    private bool _wasOnline;

    public TranscriptCorrelator Transcript { get; } = new();

    /// <summary>当前消息库。界面载入新文件时会整份换掉实例，所以每次要回复时现取。</summary>
    public Func<MessageLibrary?>? LibraryProvider { get; set; }

    /// <summary>
    /// 会话日志目录怎么来。默认放在 exe 同目录的 QoderSecsData/logs 下按配置和进程区分；
    /// 测试用它指到临时目录。
    /// </summary>
    public Func<ConnectionProfile, string>? SessionDirectoryFactory { get; set; }

    /// <summary>本次连接的磁盘日志；连接时创建，断开时关闭。没连接时为 null。</summary>
    public TranscriptLog? Log { get; private set; }

    /// <summary>本次会话的落盘目录，effective-profile.json 就在那里。没连接时为 null。</summary>
    public string? SessionDirectory { get; private set; }

    public LinkPhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    private LinkPhase _phase = LinkPhase.Offline;

    /// <summary>上过线又掉回连接中/重连中，说明对端走了。连接层用它决定要不要改报 PeerLost。</summary>
    public bool WasOnline
    {
        get
        {
            lock (_gate)
            {
                return _wasOnline;
            }
        }
    }

    public event EventHandler<LinkPhase>? PhaseChanged;

    /// <summary>
    /// 开一次会话：清掉上一条会话的记录，建目录、开两个日志、抄一份实际生效的配置。
    /// 各传输方式只要交一句自己的会话说明（地址或串口参数），其余口径在这里统一。
    /// </summary>
    public void Open(ConnectionProfile profile, string transport, string description)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // 新会话从空记录开始，和界面在连接前清空列表保持一致。
        // 不清的话，关联器会把上一次连接的记录（连同它们的行内 SML）一直挂到进程结束。
        Transcript.Clear();

        var startedAt = DateTimeOffset.Now;
        var sessionDirectory = SessionDirectoryFactory?.Invoke(profile)
            ?? new AppDataPaths(AppContext.BaseDirectory).CreateSessionDirectory(
                profile.Id,
                startedAt,
                Environment.ProcessId);

        // sml.log 的第一行是会话头：脱离目录名也能知道这份正文属于哪次会话、哪个进程。
        var header =
            $"# QoderSecs session started={startedAt:yyyy-MM-ddTHH:mm:ss.fffzzz} " +
            $"pid={Environment.ProcessId} profile={profile.Id} transport={transport}";
        var log = TranscriptLog.Open(sessionDirectory, header);

        lock (_gate)
        {
            SessionDirectory = sessionDirectory;
            Log = log;
            _inboundRequests.Clear();
        }

        WriteEffectiveProfile(sessionDirectory, profile, transport, startedAt);
        log.EnqueueDetail($"会话开始 {description}");
    }

    /// <summary>
    /// 连接层在 Open 之后失败时的收尾：把原因写进 detail.log 再关日志。
    /// 失败的那次也算一次会话，事后能看到当时配的是什么、卡在哪一步。
    /// </summary>
    public async ValueTask AbortOpenAsync(string reason)
    {
        lock (_gate)
        {
            _inboundRequests.Clear();
        }

        var log = DetachLog();
        if (log is null)
        {
            return;
        }

        log.EnqueueDetail(reason);
        await log.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 断开时的会话收尾：还挂着的记录落到「连接已断开」，状态改回离线，最后关日志。
    /// 顺序不能倒——这几行状态变化要写进本次会话的 detail.log。
    /// </summary>
    public async ValueTask CloseAsync()
    {
        lock (_gate)
        {
            _inboundRequests.Clear();
        }

        Transcript.ClearPending();
        SetPhase(LinkPhase.Offline);
        await CloseLogAsync().ConfigureAwait(false);
    }

    private async ValueTask CloseLogAsync()
    {
        var log = DetachLog();
        if (log is null)
        {
            return;
        }

        log.EnqueueDetail("会话结束");
        await log.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>取引用和置空在同一把锁里：两个线程同时断开时不能把同一个日志关两遍。</summary>
    private TranscriptLog? DetachLog()
    {
        lock (_gate)
        {
            var log = Log;
            Log = null;
            SessionDirectory = null;
            return log;
        }
    }

    public void SetPhase(LinkPhase phase)
    {
        TranscriptLog? log;
        lock (_gate)
        {
            if (_phase == phase)
            {
                return;
            }

            _phase = phase;
            if (phase == LinkPhase.Online)
            {
                _wasOnline = true;
            }
            else if (phase == LinkPhase.Offline)
            {
                _wasOnline = false;
            }

            log = Log;
        }

        log?.EnqueueDetail($"连接状态 {phase}");
        PhaseChanged?.Invoke(this, phase);
    }

    /// <summary>
    /// 按收发回调记一条。估算、格式化、落盘全在协议线程上：这里抛出去会把连接一起带崩，
    /// 所以大 PP body 出了任何问题都只降级成一条没有正文的记录。
    /// 返回的记录供调用方补自己的在途信息（HSMS 的 T3 超时要拿它认领 SystemBytes）。
    /// </summary>
    public CommunicationRecord Record(SecsDirection direction, int systemBytes, SecsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var header = new SecsHeader(message.S, message.F, message.ReplyExpected, message.Name);
        var at = DateTimeOffset.Now;
        var log = Log;

        string? inline = null;
        long? sequence = null;
        long byteLength;

        try
        {
            var estimate = SmlConverter.EstimateTextBytes(message);
            if (estimate <= TranscriptLog.InlineLimit)
            {
                inline = SafeToSml(header, message);
                byteLength = Encoding.UTF8.GetByteCount(inline);
                if (log is not null)
                {
                    sequence = log.EnqueueInlineSml(direction, systemBytes, header, inline, at);
                }
            }
            else if (log is null)
            {
                // 没有日志可落盘时也不生成整份正文：几十兆文本进了记录再进详情框，界面照样画不动。
                byteLength = estimate;
                inline = $"{header.Signature} 正文约 {SmlConverter.SizeText(estimate)}，这次会话没开日志，正文不留存。";
            }
            else if (log.WriteSmlBlocking(direction, systemBytes, header, message, at) is { } written)
            {
                // 大消息不在内存里留正文：在收发线程上流式写进 sml.log，记录只拿序号和精确长度。
                sequence = written.Sequence;
                byteLength = written.ByteLength;
            }
            else
            {
                byteLength = estimate;
                inline = $"{header.Signature} 正文约 {SmlConverter.SizeText(estimate)}，没能写进 sml.log，原因见 detail.log。";
            }
        }
        catch (Exception exception)
        {
            sequence = null;
            byteLength = 0;
            inline = $"{header.Signature} 正文没能记下来：{exception.Message}";
            log?.EnqueueDetail($"{direction} {header.Signature} sys={systemBytes:X8} 正文处理失败：{exception}");
        }

        if (direction == SecsDirection.Out)
        {
            return AttachLogDetails(Transcript.RecordOutbound(header, systemBytes, inline, at, sequence, byteLength));
        }

        if (header.ReplyExpected && !header.IsSecondary)
        {
            lock (_gate)
            {
                _inboundRequests[message] = systemBytes;
            }
        }

        return AttachLogDetails(Transcript.RecordInbound(header, systemBytes, inline, at, sequence, byteLength));
    }

    /// <summary>
    /// 收到带 W 的 Primary 时按消息库里的 AutoReply 回一条。只有库里配了才回，
    /// 回不出去就把记录标成未自动回复，不然界面上会一直挂着「待本端回复」。
    /// sendReply 由传输层给：HSMS 走 Secs4Net 的事务回复，SECS-I 走同 SystemBytes 的独立发送。
    /// </summary>
    public async Task AutoReplyAsync(
        SecsMessage primary,
        Func<SecsMessage, CancellationToken, Task<bool>> sendReply,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(sendReply);

        if (!primary.ReplyExpected)
        {
            return;
        }

        var systemBytes = TakeInboundSystemBytes(primary);
        var signature = $"S{primary.S}F{primary.F}W";
        var systemText = systemBytes?.ToString("X8") ?? "-";
        var log = Log;

        try
        {
            var library = LibraryProvider?.Invoke();
            var target = library is null ? null : AutoReplyResolver.FindTarget(library, primary.S, primary.F);
            if (target is null)
            {
                log?.EnqueueDetail(
                    $"未自动回复 收到 {signature} sys={systemText}：库里没给 S{primary.S}F{primary.F} 配 AutoReply");
                MarkAutoReplySkipped(systemBytes);
                return;
            }

            if (!SmlConverter.TryToMessage(target, replyExpected: false, out var reply, out _))
            {
                log?.EnqueueDetail(
                    $"未自动回复 收到 {signature} sys={systemText}：" +
                    $"AutoReply 指向的「{target.Name}」转不成可发送的消息");
                MarkAutoReplySkipped(systemBytes);
                return;
            }

            using (reply)
            {
                try
                {
                    if (!await sendReply(reply, cancellation).ConfigureAwait(false))
                    {
                        log?.EnqueueDetail(
                            $"未自动回复 收到 {signature} sys={systemText}：回复没发出去（连接已关闭）");
                        MarkAutoReplySkipped(systemBytes);
                        return;
                    }

                    log?.EnqueueDetail(
                        $"自动回复 收到 {signature} sys={systemText}：" +
                        $"按库里 S{primary.S}F{primary.F} 的 AutoReply 配置发出「{target.Name}」（同 sys）");
                }
                catch (Exception exception) when (exception is SecsException or OperationCanceledException or ObjectDisposedException)
                {
                    // 对端已经走了或正在断开：回不出去不是错误，记录标成未自动回复就够了。
                    log?.EnqueueDetail(
                        $"未自动回复 收到 {signature} sys={systemText}：发送异常 {exception.Message}");
                    MarkAutoReplySkipped(systemBytes);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 接收管线不能因为一条消息就停掉：消息库正被界面编辑时读它会抛 LINQ-to-XML 异常，
            // 这种情况下这条不回复就够了，后面的消息还得继续处理。
            log?.EnqueueDetail(
                $"未自动回复 收到 {signature} sys={systemText}：" +
                $"出错 {exception.GetType().Name} {exception.Message}");
            MarkAutoReplySkipped(systemBytes);
        }
    }

    private int? TakeInboundSystemBytes(SecsMessage message)
    {
        lock (_gate)
        {
            return _inboundRequests.Remove(message, out var systemBytes) ? systemBytes : null;
        }
    }

    private void MarkAutoReplySkipped(int? systemBytes)
    {
        if (systemBytes is { } bytes)
        {
            Transcript.MarkAutoReplySkipped(bytes);
        }
    }

    private static string SafeToSml(SecsHeader header, SecsMessage message)
    {
        try
        {
            return SmlConverter.ToSml(message);
        }
        catch (Exception ex)
        {
            return $"{header.Signature} 无法转成 SML：{ex.Message}";
        }
    }

    /// <summary>
    /// 把状态变化写进 detail.log。日志位置和正文长度在记录进列表之前就挂好了。
    /// 收发行带上 seq 和时刻，与 sml.log 的索引行一一对应，翻记录时不用靠 SystemBytes 猜。
    /// </summary>
    private CommunicationRecord AttachLogDetails(CommunicationRecord record)
    {
        Log?.EnqueueDetail(DetailLineOf(record));
        record.PropertyChanged += (_, e) =>
        {
            // 只跟状态变化：耗时是和状态一起写的，两个都跟会把同一件事在 detail.log 里记两遍。
            if (e.PropertyName == nameof(CommunicationRecord.Status))
            {
                Log?.EnqueueDetail(DetailLineOf(record));
            }
        };

        return record;
    }

    private static string DetailLineOf(CommunicationRecord record) =>
        $"{record.DirectionText} {record.MessageText} sys={record.SystemBytesText} " +
        $"seq={record.SmlSequence?.ToString() ?? "-"} 状态={record.StatusText}" +
        (record.ElapsedMilliseconds is { } ms ? $" 耗时={ms}ms" : string.Empty);

    /// <summary>
    /// 把这次连接真正生效的参数抄进会话目录，事后看日志不用猜当时是怎么配的。
    /// 外面包一层会话信息，这个文件就和 sml.log 的会话头、detail.log 的首行对上同一个开始时刻。
    /// </summary>
    private static void WriteEffectiveProfile(
        string sessionDirectory,
        ConnectionProfile profile,
        string transport,
        DateTimeOffset startedAt) =>
        File.WriteAllText(
            Path.Combine(sessionDirectory, "effective-profile.json"),
            JsonSerializer.Serialize(
                new EffectiveProfileSnapshot(
                    startedAt,
                    Environment.ProcessId,
                    transport,
                    profile),
                ConnectionSettingsStore.JsonOptions));

    private sealed record EffectiveProfileSnapshot(
        DateTimeOffset StartedAt,
        int ProcessId,
        string Transport,
        ConnectionProfile Profile);
}

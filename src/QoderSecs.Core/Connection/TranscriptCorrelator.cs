namespace QoderSecs.Core.Connection;

/// <summary>
/// 按 SystemBytes 把请求和回复配对。不按 SxFy 或到达时间猜测事务关系。
/// Secs4Net 只在有人等待时才把 Secondary 送进接收管线，所以孤立和迟到的回复只能从这里判断。
/// </summary>
public sealed class TranscriptCorrelator
{
    /// <summary>
    /// 内存里长期保留多少条通信记录。上限按 16KB 那条分档算：正文不超过
    /// TranscriptLog.InlineLimit 的记录会在内存里留整份 SML，5000 条最坏约 80MB，上限按这条算档取。
    /// </summary>
    public const int DefaultRecordCap = 5000;

    private readonly object _gate = new();
    private readonly List<CommunicationRecord> _records = [];
    private readonly Dictionary<int, CommunicationRecord> _outstandingRequests = [];
    private readonly Dictionary<int, CommunicationRecord> _timedOutRequests = [];
    private readonly Dictionary<int, CommunicationRecord> _awaitingLocalReply = [];

    /// <summary>保留多少条。设成 0 或负数就是不设上限。</summary>
    public int RecordCap { get; set; } = DefaultRecordCap;

    /// <summary>因为超出上限被丢掉的老记录条数，界面拿它说一句「已丢弃 N 条」。</summary>
    public int DiscardedRecords { get; private set; }

    public IReadOnlyList<CommunicationRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToArray();
            }
        }
    }

    public event Action<CommunicationRecord>? RecordAdded;

    /// <summary>记录被上限挤掉时通知，界面那一份集合要跟着删，不然两边条数不一致。</summary>
    public event Action<CommunicationRecord>? RecordRemoved;

    /// <summary>
    /// 记一条发出去的消息。日志位置要在记录进列表之前就挂好：RecordAdded 一响，
    /// 别人立刻就能读这条记录，晚一步赋值读到的就是没有正文位置的半条。
    /// </summary>
    public CommunicationRecord RecordOutbound(
        SecsHeader header,
        int systemBytes,
        string? sml,
        DateTimeOffset timestamp,
        long? smlSequence = null,
        long? smlByteLength = null)
    {
        var status = header.ReplyExpected && !header.IsSecondary
            ? RecordStatus.AwaitingSecondary
            : RecordStatus.Sent;
        var record = Add(new CommunicationRecord(SecsDirection.Out, systemBytes, header, timestamp, sml, status)
        {
            SmlSequence = smlSequence,
            SmlByteLength = smlByteLength
        });

        lock (_gate)
        {
            if (status == RecordStatus.AwaitingSecondary)
            {
                _outstandingRequests[systemBytes] = record;
            }
            else if (header.IsSecondary && _awaitingLocalReply.Remove(systemBytes, out var request))
            {
                request.Status = RecordStatus.Answered;
            }
        }

        return record;
    }

    public CommunicationRecord RecordInbound(
        SecsHeader header,
        int systemBytes,
        string? sml,
        DateTimeOffset timestamp,
        long? smlSequence = null,
        long? smlByteLength = null)
    {
        if (header.IsSecondary)
        {
            CommunicationRecord? request = null;
            lock (_gate)
            {
                if (_outstandingRequests.Remove(systemBytes, out var pending))
                {
                    request = pending;
                }
                else if (_timedOutRequests.Remove(systemBytes, out var timedOut))
                {
                    request = timedOut;
                }
            }

            if (request is null)
            {
                return Add(new CommunicationRecord(
                    SecsDirection.In, systemBytes, header, timestamp, sml, RecordStatus.OrphanSecondary)
                {
                    SmlSequence = smlSequence,
                    SmlByteLength = smlByteLength
                });
            }

            var isLate = request.Status is RecordStatus.TimedOut or RecordStatus.WaitAbandoned;
            var record = Add(new CommunicationRecord(
                SecsDirection.In, systemBytes, header, timestamp, sml,
                isLate ? RecordStatus.LateSecondary : RecordStatus.PairedSecondary)
            {
                SmlSequence = smlSequence,
                SmlByteLength = smlByteLength,
                LinkedSystemBytes = systemBytes
            });

            request.ElapsedMilliseconds = (int)Math.Round((timestamp - request.Timestamp).TotalMilliseconds);
            if (!isLate)
            {
                request.Status = RecordStatus.SecondaryReceived;
            }

            return record;
        }

        var inboundStatus = header.ReplyExpected ? RecordStatus.AwaitingReply : RecordStatus.Received;
        var inbound = Add(new CommunicationRecord(SecsDirection.In, systemBytes, header, timestamp, sml, inboundStatus)
        {
            SmlSequence = smlSequence,
            SmlByteLength = smlByteLength
        });
        if (inboundStatus == RecordStatus.AwaitingReply)
        {
            lock (_gate)
            {
                _awaitingLocalReply[systemBytes] = inbound;
            }
        }

        return inbound;
    }

    /// <summary>T3 到点但仍允许迟到回复继续配对。</summary>
    public void MarkTimedOut(int systemBytes, DateTimeOffset timestamp) =>
        StopWaiting(systemBytes, timestamp, RecordStatus.TimedOut);

    /// <summary>
    /// 本端不等这条请求的回复了（点「取消等待」）。走的是 T3 超时那一档的登记，
    /// 回复要是稍后真到了，仍然按迟到回复配上，不写成无人等待。
    /// </summary>
    public void MarkWaitAbandoned(int systemBytes, DateTimeOffset timestamp) =>
        StopWaiting(systemBytes, timestamp, RecordStatus.WaitAbandoned);

    private void StopWaiting(int systemBytes, DateTimeOffset timestamp, RecordStatus status)
    {
        lock (_gate)
        {
            if (!_outstandingRequests.Remove(systemBytes, out var record))
            {
                return;
            }

            // 耗时先写再改状态：detail.log 只在状态变化时记一行，
            // 反过来会先记一条没有耗时的，再记一条有耗时的，同一件事写两遍。
            record.ElapsedMilliseconds = (int)Math.Round((timestamp - record.Timestamp).TotalMilliseconds);
            record.Status = status;
            _timedOutRequests[systemBytes] = record;
        }
    }

    /// <summary>
    /// 这条消息没能送上线路。请求从待回复表里摘掉，免得下一次同 SystemBytes 的回复被配到一条
    /// 根本没发出去的消息上；状态写「发送失败」。
    /// </summary>
    public void MarkSendFailed(CommunicationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            if (_outstandingRequests.TryGetValue(record.SystemBytes, out var pending)
                && ReferenceEquals(pending, record))
            {
                _outstandingRequests.Remove(record.SystemBytes);
            }

            record.Status = RecordStatus.SendFailed;
        }
    }

    /// <summary>这条 Primary 不回复了。已经回过的（不在待回复表里）不动，免得把「已自动回复」盖掉。</summary>
    public void MarkAutoReplySkipped(int systemBytes)
    {
        lock (_gate)
        {
            if (_awaitingLocalReply.Remove(systemBytes, out var request))
            {
                request.Status = RecordStatus.AutoReplySkipped;
            }
        }
    }

    /// <summary>
    /// 断线后没人再等这些请求：把还挂着「等待回复」「待本端回复」的记录标成连接已断开，
    /// 再清空配对表，避免下一次连接把它们配错。
    /// </summary>
    public void ClearPending()
    {
        List<CommunicationRecord> stuck;
        lock (_gate)
        {
            stuck = _outstandingRequests.Values.Concat(_awaitingLocalReply.Values).ToList();
            _outstandingRequests.Clear();
            _timedOutRequests.Clear();
            _awaitingLocalReply.Clear();
        }

        foreach (var record in stuck)
        {
            record.Status = RecordStatus.LinkClosed;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
            _outstandingRequests.Clear();
            _timedOutRequests.Clear();
            _awaitingLocalReply.Clear();
            DiscardedRecords = 0;
        }
    }

    private CommunicationRecord Add(CommunicationRecord record)
    {
        List<CommunicationRecord> dropped = [];
        lock (_gate)
        {
            _records.Add(record);
            TrimLocked(dropped);
        }

        RecordAdded?.Invoke(record);

        // 丢弃通知放在锁外：界面的集合是 Dispatcher 那边的事，拿着协议的锁回去调它不安全。
        foreach (var droppedRecord in dropped)
        {
            DiscardedRecords++;
            RecordRemoved?.Invoke(droppedRecord);
        }

        return record;
    }

    /// <summary>
    /// 从最老的开始丢，丢到不超过上限为止。
    /// 还挂在三本配对表里的记录不能丢：请求被丢了它的回复就配不上，
    /// 标成超时的丢了那条迟到回复也没地方挂——配对正确性比条数重要，
    /// 所以极端情况下（几千条都没回）实际条数会顶在之上。
    /// 最新那一条也留着：调用方是 Add 里先记账、登记配对表在 Add 之后，
    /// 万一前面全挂着，把刚交给调用方那条挤掉就等于返回了一个列表里找不到的记录。
    /// </summary>
    private void TrimLocked(List<CommunicationRecord> dropped)
    {
        var cap = RecordCap;
        if (cap <= 0 || _records.Count <= cap)
        {
            return;
        }

        var overflow = _records.Count - cap;
        for (var i = 0; i < _records.Count - 1 && dropped.Count < overflow; i++)
        {
            var candidate = _records[i];
            if (_outstandingRequests.ContainsValue(candidate)
                || _timedOutRequests.ContainsValue(candidate)
                || _awaitingLocalReply.ContainsValue(candidate))
            {
                continue;
            }

            dropped.Add(candidate);
        }

        foreach (var record in dropped)
        {
            _records.Remove(record);
        }
    }
}

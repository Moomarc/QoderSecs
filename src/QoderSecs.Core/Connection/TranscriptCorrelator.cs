namespace QoderSecs.Core.Connection;

/// <summary>
/// 按 SystemBytes 把请求和回复配对。不按 SxFy 或到达时间猜测事务关系。
/// Secs4Net 只在有人等待时才把 Secondary 送进接收管线，所以孤立和迟到的回复只能从这里判断。
/// </summary>
public sealed class TranscriptCorrelator
{
    private readonly object _gate = new();
    private readonly List<CommunicationRecord> _records = [];
    private readonly Dictionary<int, CommunicationRecord> _outstandingRequests = [];
    private readonly Dictionary<int, CommunicationRecord> _timedOutRequests = [];
    private readonly Dictionary<int, CommunicationRecord> _awaitingLocalReply = [];

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

    public CommunicationRecord RecordOutbound(SecsHeader header, int systemBytes, string? sml, DateTimeOffset timestamp)
    {
        var status = header.ReplyExpected && !header.IsSecondary
            ? RecordStatus.AwaitingSecondary
            : RecordStatus.Sent;
        var record = Add(new CommunicationRecord(SecsDirection.Out, systemBytes, header, timestamp, sml, status));

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

    public CommunicationRecord RecordInbound(SecsHeader header, int systemBytes, string? sml, DateTimeOffset timestamp)
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
                return Add(new CommunicationRecord(SecsDirection.In, systemBytes, header, timestamp, sml, RecordStatus.OrphanSecondary));
            }

            var isLate = request.Status == RecordStatus.TimedOut;
            var record = Add(new CommunicationRecord(
                SecsDirection.In, systemBytes, header, timestamp, sml,
                isLate ? RecordStatus.LateSecondary : RecordStatus.PairedSecondary));

            record.LinkedSystemBytes = systemBytes;
            request.ElapsedMilliseconds = (int)Math.Round((timestamp - request.Timestamp).TotalMilliseconds);
            if (!isLate)
            {
                request.Status = RecordStatus.SecondaryReceived;
            }

            return record;
        }

        var inboundStatus = header.ReplyExpected ? RecordStatus.AwaitingReply : RecordStatus.Received;
        var inbound = Add(new CommunicationRecord(SecsDirection.In, systemBytes, header, timestamp, sml, inboundStatus));
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
    public void MarkTimedOut(int systemBytes, DateTimeOffset timestamp)
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
            record.Status = RecordStatus.TimedOut;
            _timedOutRequests[systemBytes] = record;
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
        }
    }

    private CommunicationRecord Add(CommunicationRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
        }

        RecordAdded?.Invoke(record);
        return record;
    }
}
